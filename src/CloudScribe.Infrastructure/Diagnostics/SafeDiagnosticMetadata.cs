using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Only product-owned event schemas and validated primitive fields enter ordinary logs.</summary>
internal static partial class SafeDiagnosticMetadata
{
    private sealed record Schema(string Name, string Template, IReadOnlyDictionary<string, Type> Fields);
    private static readonly Dictionary<int, Schema> Schemas = ReadSchemas();
    private static readonly HashSet<string> CatalogCategories = new(StringComparer.Ordinal)
    { "saved-credential-unavailable", "cancelled-or-timeout", "http-rejected", "transport", "storage", "invalid-json", "credential-crypto", "local-access", "invalid-data", "verification", "timeout", "unexpected" };
    private static readonly HashSet<string> StopCategories = new(StringComparer.Ordinal)
    { "caller-cancelled", "guard-or-execution-failure", "private-diagnostics-not-ready", "article-receipt-unavailable" };
    private static readonly Dictionary<string, HashSet<string>> SafeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["InitializerName"] = new(StringComparer.Ordinal) { "DatabaseInitializer" },
        ["Route"] = new(StringComparer.Ordinal) { "Studio", "Library", "Queue", "Audio", "Pricing", "Settings", "Diagnostics" },
        ["LifecycleState"] = new(StringComparer.Ordinal) { "Ready", "Empty", "Loading", "Offline", "Error" },
        ["Theme"] = new(StringComparer.Ordinal) { "FollowSystem", "CosmicNight", "CosmicPaper", "HighContrast" },
        ["LayoutMode"] = new(StringComparer.Ordinal) { "Narrow", "Compact", "Standard", "Full" },
        ["LocationMode"] = new(StringComparer.Ordinal) { "configured-application-data", "executable-folder", "local-app-data-fallback" },
        ["FallbackReason"] = new(StringComparer.Ordinal) { "none", "UnauthorizedAccessException", "IOException", "SecurityException", "NotSupportedException", "ArgumentException" },
    };

    internal static SafeDiagnosticMetadataSnapshot Capture<TState>(EventId id, TState state)
    {
        if (!Schemas.TryGetValue(id.Id, out var schema)) return Unknown();
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool complete = true;
        try
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                int count = 0;
                foreach (var item in values)
                {
                    if (++count > 64) { complete = false; break; }
                    if (string.Equals(item.Key, "{OriginalFormat}", StringComparison.Ordinal)) continue;
                    if (!schema.Fields.TryGetValue(item.Key, out var expected)
                        || !TryValue(item.Key, expected, item.Value, out string? safe))
                    { complete = false; continue; }
                    if (!fields.TryAdd(item.Key, safe)) complete = false;
                }
            }
            else complete = schema.Fields.Count == 0;
        }
        catch (Exception error) when (NonFatal(error)) { complete = false; }
        complete &= schema.Fields.Keys.All(fields.ContainsKey);
        return Build(id.Id, schema, fields, complete);
    }

    internal static SafeDiagnosticMetadataSnapshot Export(int eventId, IReadOnlyDictionary<string, string?> supplied)
    {
        if (!Schemas.TryGetValue(eventId, out var schema)) return Unknown();
        var safe = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool complete = true;
        foreach (var item in supplied.Take(65))
        {
            // Hashes correlate local wire evidence but are not anonymization or routine export content.
            if (item.Key.EndsWith("Sha256", StringComparison.OrdinalIgnoreCase)) { complete = false; continue; }
            if (!schema.Fields.TryGetValue(item.Key, out var expected) || !TryText(item.Key, expected, item.Value, out var value))
            { complete = false; continue; }
            if (!safe.TryAdd(item.Key, value)) complete = false;
        }
        complete &= schema.Fields.Keys.All(safe.ContainsKey);
        return Build(eventId, schema, safe, complete);
    }

    private static SafeDiagnosticMetadataSnapshot Build(int id, Schema schema, Dictionary<string, string?> fields, bool complete)
    {
        string rendered = Placeholder().Replace(schema.Template, match => fields.TryGetValue(match.Groups["name"].Value, out var value)
            ? value ?? "[not observed]" : "[omitted]");
        return new(schema.Name, Component(id), rendered.Length <= 8192 ? rendered : "Diagnostic message exceeded its bound.", fields, complete);
    }

    private static SafeDiagnosticMetadataSnapshot Unknown() => new("UnregisteredEvent", "Unclassified",
        "Unregistered free-form diagnostic text omitted. Inspect safe incident metadata; details are incomplete.",
        new Dictionary<string, string?>(StringComparer.Ordinal), false);

    private static string Component(int id) => id switch
    {
        >= 1000 and < 1100 => "Startup",
        >= 1100 and < 1200 => "Persistence",
        >= 1200 and < 1300 => "Activation",
        >= 1300 and < 1400 => "Interface",
        >= 1400 and < 1500 => "Observability",
        >= 1500 and < 1600 => "Preferences",
        >= 1600 and < 1700 => "GoogleSynthesis",
        >= 6200 and < 6300 => "GoogleCatalog",
        6300 => "LocalWorkflow",
        _ => "Product",
    };

    private static bool TryValue(string key, Type expected, object? value, out string? safe)
    {
        safe = null;
        Type actual = Nullable.GetUnderlyingType(expected) ?? expected;
        if (value is null) return !actual.IsValueType || Nullable.GetUnderlyingType(expected) is not null;
        if (value.GetType() != actual) return false;
        if (actual.IsEnum && Enum.IsDefined(actual, value)) { safe = value.ToString(); return true; }
        if (value is Guid guid) { safe = guid.ToString("D"); return true; }
        if (value is DateTimeOffset time) { safe = time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); return true; }
        if (value is bool boolean) { safe = boolean ? "true" : "false"; return true; }
        if (value is string text) return TryString(key, text, out safe);
        if (value is byte or short or int or long or ushort or uint or ulong or decimal)
        { safe = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture); return safe.Length <= 32; }
        if (value is double number && double.IsFinite(number))
        { safe = number.ToString("R", CultureInfo.InvariantCulture); return safe.Length <= 32; }
        return false;
    }

    private static bool TryString(string key, string text, out string? safe)
    {
        safe = null;
        if (text.Length > 128) return false;
        if (SafeNames.TryGetValue(key, out var names) && names.Contains(text)) { safe = text; return true; }
        if (key.EndsWith("Sha256", StringComparison.OrdinalIgnoreCase) && text.Length == 64 && text.All(char.IsAsciiHexDigit))
        { safe = text.ToLowerInvariant(); return true; }
        if (string.Equals(key, "Correlation", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(text, out var id))
        { safe = id.ToString("N"); return true; }
        if (string.Equals(key, "DiagnosticCode", StringComparison.OrdinalIgnoreCase)
            && string.Equals(GoogleGenerationOutcomeDiagnostic.Sanitize(text), text, StringComparison.Ordinal))
        { safe = text; return true; }
        if (string.Equals(key, "CatalogErrorCategory", StringComparison.OrdinalIgnoreCase) && CatalogCategories.Contains(text)
            || string.Equals(key, "SafeCategory", StringComparison.OrdinalIgnoreCase) && StopCategories.Contains(text)
            || string.Equals(key, "Mode", StringComparison.OrdinalIgnoreCase) && text is "offline" or "online")
        { safe = text; return true; }
        return false;
    }

    private static bool TryText(string key, Type expected, string? text, out string? safe)
    {
        safe = null;
        Type actual = Nullable.GetUnderlyingType(expected) ?? expected;
        if (text is null) return !actual.IsValueType || Nullable.GetUnderlyingType(expected) is not null;
        if (text.Length > 128) return false;
        object? value = null;
        if (actual == typeof(string)) return TryString(key, text, out safe);
        if (actual.IsEnum && Enum.TryParse(actual, text, ignoreCase: false, out var enumeration)) value = enumeration;
        else if (actual == typeof(Guid) && Guid.TryParseExact(text, "D", out var id)) value = id;
        else if (actual == typeof(DateTimeOffset) && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) value = time;
        else if (actual == typeof(bool) && bool.TryParse(text, out bool boolean)) value = boolean;
        else if (actual == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer)) value = integer;
        else if (actual == typeof(long) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long large)) value = large;
        else if (actual == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) value = number;
        return value is not null && TryValue(key, expected, value, out safe);
    }

    private static Dictionary<int, Schema> ReadSchemas()
    {
        var result = new Dictionary<int, Schema>();
        foreach (var type in new[] { typeof(CloudScribeLog), typeof(GoogleCatalogRefreshDiagnostics), typeof(LocalRequestDiagnostics) })
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attribute = method.GetCustomAttribute<LoggerMessageAttribute>();
                if (attribute?.Message is not { Length: <= 8192 } template || attribute.EventId < 0) continue;
                var parameters = method.GetParameters().ToDictionary(p => p.Name!, p => p.ParameterType, StringComparer.OrdinalIgnoreCase);
                var fields = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (Match match in Placeholder().Matches(template))
                {
                    string name = match.Groups["name"].Value;
                    if (parameters.TryGetValue(name, out var expected)) fields[name] = expected;
                }
                if (!result.TryAdd(attribute.EventId, new(method.Name, template, fields))) throw new InvalidOperationException("Duplicate product diagnostic schema.");
            }
        return result;
    }

    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
    [GeneratedRegex("\\{(?<name>[A-Za-z][A-Za-z0-9_]*)(?:[^{}]*)\\}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Placeholder();
}

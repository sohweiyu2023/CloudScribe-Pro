using System.Globalization;
using System.Text.Json;
using CloudScribe.Application.Startup;

namespace CloudScribe.Infrastructure.Diagnostics;

public static partial class SafeDiagnosticExportEncoder
{
    private static SafeExceptionSummary? ReadException(JsonElement root)
    {
        string? encoded = Text(root, "ExceptionDetail");
        if (!string.Equals(Text(root, "ExceptionDetailFormat"), "safe-exception-summary-v1", StringComparison.Ordinal) || encoded is not { Length: <= 8192 }) return null;
        try
        {
            using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions { MaxDepth = 8 });
            var data = document.RootElement; int nodes = 0;
            if (data.ValueKind != JsonValueKind.Object || !ValidateNode(data, ref nodes) || Integer(data, "SchemaVersion") != 1
                || !TryEnum<SafeExceptionCategory>(data, "Category", out var category) || Integer(data, "ErrorCode") is not { } error
                || !TryEnum<SafeExceptionStackState>(data, "StackState", out var stack)
                || !IsBoolean(data, "Truncated") || !IsBoolean(data, "MetadataIncomplete")
                || !NullableCode(data, "NativeErrorCode", database: false) || !NullableCode(data, "SqlitePrimaryCode", database: true)
                || !NullableCode(data, "SqliteExtendedCode", database: true)) return null;
            var locations = ReadLocations(data); var causes = ReadCauses(data);
            if (stack == SafeExceptionStackState.Available && locations.Count == 0 || stack == SafeExceptionStackState.Missing && locations.Count != 0) return null;
            if (locations.Any(location => location.CauseIndex > causes.Count)) return null;
            return new(1, category, error, Integer(data, "NativeErrorCode"), DatabaseCode(data, "SqlitePrimaryCode"), DatabaseCode(data, "SqliteExtendedCode"),
                stack, locations, causes, Boolean(data, "Truncated"), Boolean(data, "MetadataIncomplete"));
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException) { return null; }
    }

    private static List<SafeExceptionLocation> ReadLocations(JsonElement data)
    {
        var result = new List<SafeExceptionLocation>();
        if (!data.TryGetProperty("Locations", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > SafeExceptionSummarizer.MaximumLocations) throw new FormatException("Unsupported location schema.");
        foreach (var item in array.EnumerateArray())
        {
            if (Integer(item, "CauseIndex") is not (>= 0 and < SafeExceptionSummarizer.MaximumCauses and var cause)
                || Integer(item, "MethodToken") is not { } token || (token & unchecked((int)0xff000000)) != 0x06000000 || (token & 0x00ffffff) == 0
                || Integer(item, "IlOffset") is not (>= -1 and var offset) || !Guid.TryParse(Text(item, "ModuleId"), out var module) || module == Guid.Empty)
                throw new FormatException("Unsupported location identity.");
            result.Add(new(cause, module, token, offset));
        }
        return result;
    }

    private static List<SafeExceptionCause> ReadCauses(JsonElement data)
    {
        var result = new List<SafeExceptionCause>();
        if (!data.TryGetProperty("InnerCauses", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() >= SafeExceptionSummarizer.MaximumCauses) throw new FormatException("Unsupported cause schema.");
        foreach (var item in array.EnumerateArray())
        {
            if (!TryEnum<SafeExceptionCategory>(item, "Category", out var category) || Integer(item, "ErrorCode") is not { } code) throw new FormatException("Unsupported cause fields.");
            result.Add(new(category, code));
        }
        return result;
    }

    private static int? DatabaseCode(JsonElement root, string key) => Integer(root, key) is >= 0 and <= 65_535 and var value ? value : null;
    private static bool NullableCode(JsonElement root, string key, bool database) => root.TryGetProperty(key, out var value)
        && (value.ValueKind == JsonValueKind.Null || Integer(root, key) is { } number && (!database || number is >= 0 and <= 65_535));
    private static bool IsBoolean(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool TryEnum<T>(JsonElement root, string key, out T value) where T : struct, Enum
    {
        value = default;
        if (Integer(root, key) is not { } number) return false;
        value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value);
    }

    private static SafeStartupPhaseObservation[] ReadPhases(JsonElement root)
    {
        if (!root.TryGetProperty("StartupPhases", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 64) throw new FormatException("Unsupported phase history.");
        var result = new List<SafeStartupPhaseObservation>(64);
        foreach (var item in array.EnumerateArray())
        {
            if (Integer(item, "Sequence") is not (>= 1 and <= 64 and var sequence)
                || !TryEnum<StartupPhase>(item, "Phase", out var phase) || !TryEnum<StartupPhaseState>(item, "State", out var state)
                || !TryEnum<StartupBudgetState>(item, "Budget", out var budget)
                || PositiveLong(item, "PhaseMilliseconds") is not { } phaseMs || PositiveLong(item, "StartupMilliseconds") is not { } startupMs
                || result.Count != 0 && sequence <= result[^1].Sequence) throw new FormatException("Unsupported phase fields.");
            result.Add(new(sequence, phase, state, phaseMs, startupMs, budget));
        }
        return result.ToArray();
    }
}

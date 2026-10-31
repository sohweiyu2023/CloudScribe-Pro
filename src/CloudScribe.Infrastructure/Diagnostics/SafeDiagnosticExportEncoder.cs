using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Regenerates bounded safe records. Never copies a source message, category or exception string.</summary>
public static partial class SafeDiagnosticExportEncoder
{
    public const int MaximumRecordBytes = 32_768;

    public static byte[] Encode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumRecordBytes) return Omitted("OversizedRecord");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            int nodes = 0;
            if (root.ValueKind != JsonValueKind.Object || !ValidateNode(root, ref nodes)) return Omitted("MalformedRecord");
            return EncodeObject(root);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException)
        { return Omitted("MalformedOrLegacyText"); }
    }

    public static byte[] Omitted(string state) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        SchemaVersion = 2,
        RecordState = state is "OversizedRecord" or "MalformedRecord" or "RecordLimit" or "TruncatedRecord" ? state : "MalformedOrLegacyText",
        FieldsComplete = false,
        Message = "Unsafe or unsupported diagnostic detail omitted. Original records remain local and unchanged.",
    });

    private static byte[] EncodeObject(JsonElement root)
    {
        int version = Integer(root, "SchemaVersion") ?? 0;
        bool current = version == 2;
        bool bootstrap = current && Text(root, "Kind") is "Bootstrap";
        int eventId = Integer(root, "EventId") ?? 0;
        var supplied = current ? ReadFields(root) : new Dictionary<string, string?>(StringComparer.Ordinal);
        var normal = SafeDiagnosticMetadata.Export(eventId, supplied);
        var boot = bootstrap ? SafeBootstrapMetadata.Export(Text(root, "EventName") ?? string.Empty, supplied) : null;
        var phases = bootstrap && string.Equals(boot!.EventName, "startup.phase-summary", StringComparison.Ordinal) ? ReadPhases(root) : [];
        var exception = current ? ReadException(root) : null;
        bool hasException = root.TryGetProperty("ExceptionDetail", out var detail) && detail.ValueKind is not JsonValueKind.Null;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            RecordState = current ? "CurrentMetadata" : version == 0 ? "LegacyMetadataOnly" : "UnsupportedSchemaMetadataOnly",
            TimestampUnixMilliseconds = UnixTime(root),
            ProcessId = PositiveInteger(root, "ProcessId"),
            Sequence = PositiveLong(root, "Sequence"),
            SessionId = GuidText(root, "SessionId"),
            ApplicationVersion = BuildVersion(Text(root, "ApplicationVersion")),
            RuntimeVersion = BuildVersion(Text(root, "RuntimeVersion")),
            ModuleId = GuidText(root, "ModuleId"),
            TraceId = HexIdentity(root, "TraceId", 32),
            SpanId = HexIdentity(root, "SpanId", 16),
            Level = Level(root),
            EventId = eventId,
            EventName = boot?.EventName ?? normal.EventName,
            Category = bootstrap ? "Bootstrap" : normal.Component,
            Message = current && !bootstrap ? normal.Message : "Free-form historical/bootstrap text omitted; inspect validated metadata and missing-evidence flags.",
            SafeFields = bootstrap ? (object)boot!.Fields : normal.Fields,
            StartupPhases = phases,
            FieldsComplete = current && Boolean(root, "FieldsComplete") && (boot?.FieldsComplete ?? normal.FieldsComplete),
            ExceptionEvidence = !hasException ? "Absent" : !current ? "LegacyOmitted" : exception is null ? "InvalidOrUnavailable" : "SafeSummary",
            ExceptionSummary = exception,
        });
    }

    private static Dictionary<string, string?> ReadFields(JsonElement root)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!root.TryGetProperty("SafeFields", out var fields) || fields.ValueKind != JsonValueKind.Object) return result;
        foreach (var item in fields.EnumerateObject())
        {
            if (result.Count >= 64 || item.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new FormatException("Unsupported field schema.");
            result.Add(item.Name, item.Value.GetString());
        }
        return result;
    }

    private static bool ValidateNode(JsonElement node, ref int count)
    {
        if (++count > 1024) return false;
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
                if (property.Name.Length > 128 || names.Count >= 64 || !names.Add(property.Name) || !ValidateNode(property.Value, ref count)) return false;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            if (node.GetArrayLength() > 256) return false;
            foreach (var item in node.EnumerateArray()) if (!ValidateNode(item, ref count)) return false;
        }
        return true;
    }

    private static int? Integer(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    private static long? Long(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
    private static int? PositiveInteger(JsonElement root, string key) => Integer(root, key) is >= 0 and var value ? value : null;
    private static long? PositiveLong(JsonElement root, string key) => Long(root, key) is >= 0 and var value ? value : null;
    private static long? UnixTime(JsonElement root) => Long(root, "TimestampUnixMilliseconds") is >= -62_135_596_800_000 and <= 253_402_300_799_999 and var value ? value : null;
    private static bool Boolean(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? GuidText(JsonElement root, string key) => Guid.TryParse(Text(root, key), out var id) ? id.ToString("D") : null;
    private static string? Level(JsonElement root) => Enum.TryParse<LogLevel>(Text(root, "Level"), out var level) && Enum.IsDefined(level) ? level.ToString() : null;
    private static string? HexIdentity(JsonElement root, string key, int length) => Text(root, key) is { } value && value.Length == length && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : null;
    private static string? BuildVersion(string? text)
    {
        if (text is not { Length: <= 128 }) return null;
        string[] parts = text.Split('+');
        if (parts.Length > 2 || !Version.TryParse(parts[0], out var version)) return null;
        if (parts.Length == 1) return version.ToString();
        return parts[1].Length is >= 7 and <= 64 && parts[1].All(char.IsAsciiHexDigit) ? version + "+" + parts[1].ToLowerInvariant() : null;
    }
}

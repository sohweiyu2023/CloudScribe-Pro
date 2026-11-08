using System.Globalization;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Diagnostics;

public static partial class SafeDiagnosticExportEncoder
{
    private static byte[] EncodeTrendObject(JsonElement root)
    {
        string? kind = Text(root, "Kind");
        int? id = Integer(root, "EventId");
        if (string.Equals(kind, "Event", StringComparison.Ordinal) && id is { } eventId && GoogleTrendHistoryLoggerProvider.IsTrendEvent(eventId))
        {
            var safe = SafeDiagnosticMetadata.Export(eventId, ReadFields(root));
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 2,
                RecordState = "TrendMetadata",
                TimestampUnixMilliseconds = TrendTimestamp(root),
                ApplicationVersion = BuildVersion(Text(root, "ApplicationVersion")),
                ModuleId = GuidText(root, "ComponentBuildId"),
                EventId = eventId,
                safe.EventName,
                Category = safe.Component,
                safe.Message,
                SafeFields = safe.Fields,
                FieldsComplete = Boolean(root, "FieldsComplete") && safe.FieldsComplete,
            });
        }
        bool session = string.Equals(kind, "Session", StringComparison.Ordinal) && string.Equals(Text(root, "EventName"), "HistoryOpened", StringComparison.Ordinal);
        bool gap = string.Equals(kind, "Gap", StringComparison.Ordinal) && string.Equals(Text(root, "EventName"), "InterruptedTail", StringComparison.Ordinal)
            && string.Equals(Text(root, "GapReason"), "PreviousTailIncomplete", StringComparison.Ordinal);
        if (!session && !gap) return Omitted("MalformedRecord");
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            RecordState = session ? "TrendSession" : "TrendGap",
            TimestampUnixMilliseconds = TrendTimestamp(root),
            ApplicationVersion = BuildVersion(Text(root, "ApplicationVersion")),
            ModuleId = GuidText(root, "ComponentBuildId"),
            EventName = session ? "HistoryOpened" : "InterruptedTail",
            Message = session ? "Safe trend history opened." : "The preceding history tail was incomplete; original bytes remain local and unchanged.",
            FieldsComplete = session && Boolean(root, "FieldsComplete") && ReadFields(root).Count == 0,
        });
    }

    private static long? TrendTimestamp(JsonElement root) => Text(root, "TimestampUtc") is { Length: <= 64 } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToUnixTimeMilliseconds() : null;
}

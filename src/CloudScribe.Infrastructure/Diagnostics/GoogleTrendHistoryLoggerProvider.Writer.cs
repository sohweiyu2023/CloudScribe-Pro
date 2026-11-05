using System.Text.Json;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed partial class GoogleTrendHistoryLoggerProvider
{
    private void OpenHistory(string path)
    {
        RequirePhysical(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RequirePhysical(path);
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.None);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            partialTail = stream.ReadByte() != (byte)'\n';
        }
        stream.Seek(0, SeekOrigin.End);
        if (partialTail)
        {
            stream.WriteByte((byte)'\n');
            Append(new(1, clock.GetUtcNow(), "Gap", null, "InterruptedTail", "GoogleSynthesis",
                new Dictionary<string, string?>(StringComparer.Ordinal), false, "PreviousTailIncomplete"));
        }
        Append(new(1, clock.GetUtcNow(), "Session", null, "HistoryOpened", "GoogleSynthesis",
            new Dictionary<string, string?>(StringComparer.Ordinal), true, null));
    }

    private void Append(TrendRecord record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > 32768) throw new InvalidDataException("Safe trend metadata exceeds its record bound.");
        var target = stream ?? throw new InvalidOperationException("The trend writer is not open.");
        target.Write(bytes); target.WriteByte((byte)'\n'); target.Flush(flushToDisk: true);
        recordsWritten++;
    }

    private static void RequirePhysical(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked trend history is refused.");
    }

    private sealed record TrendRecord(int Version, DateTimeOffset TimestampUtc, string Kind, int? EventId,
        string EventName, string Module, IReadOnlyDictionary<string, string?> SafeFields, bool FieldsComplete, string? GapReason)
    {
        public string ApplicationVersion { get; } = typeof(GoogleTrendHistoryLoggerProvider).Assembly.GetName().Version?.ToString() ?? "Unknown";
        public Guid ComponentBuildId { get; } = typeof(GoogleTrendHistoryLoggerProvider).Module.ModuleVersionId;
    }
}

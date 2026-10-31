namespace CloudScribe.Infrastructure.Diagnostics;

internal sealed record DiagnosticLogRecord(
    long Sequence,
    long TimestampUnixMilliseconds,
    string ApplicationVersion,
    string SessionId,
    int ProcessId,
    int ManagedThreadId,
    string Level,
    int EventId,
    string EventName,
    string Category,
    string Message,
    string? ExceptionType,
    string? ExceptionDetail,
    string? TraceId,
    string? SpanId)
{
    public int SchemaVersion { get; init; } = 2;
    public string ExceptionDetailFormat { get; init; } = "safe-exception-summary-v1";
    public IReadOnlyDictionary<string, string?> SafeFields { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);
    public bool FieldsComplete { get; init; }
    public string RuntimeVersion { get; init; } = Environment.Version.ToString();
    public Guid ModuleId { get; init; } = typeof(DiagnosticLogRecord).Module.ModuleVersionId;
}

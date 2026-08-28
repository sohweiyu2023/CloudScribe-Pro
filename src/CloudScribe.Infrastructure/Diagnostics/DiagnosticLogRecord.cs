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
    string? SpanId);

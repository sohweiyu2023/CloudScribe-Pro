using CloudScribe.Application.Diagnostics;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Permanent safe trend metadata. No free-form log text, exception detail or private response is serialized.</summary>
public sealed partial class GoogleTrendHistoryLoggerProvider : ILoggerProvider, IGoogleTrendHistoryHealth
{
    private readonly System.Threading.Lock gate = new();
    private readonly TimeProvider clock;
    private FileStream? stream;
    private bool available, disposed, partialTail;
    private string failureReason = "NotInitialized";
    private int? failureHResult;
    private long recordsWritten;
    public bool IsAvailable { get { lock (gate) return available; } }
    public string FailureReason { get { lock (gate) return failureReason; } }
    public int? LastFailureHResult { get { lock (gate) return failureHResult; } }
    public bool HasPartialTail { get { lock (gate) return partialTail; } }
    public long RecordsWritten { get { lock (gate) return recordsWritten; } }

    public GoogleTrendHistoryLoggerProvider(AppPaths paths, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        lock (gate)
        {
            try
            {
                OpenHistory(Path.Combine(paths.RootDirectory, "data", "google-trend-history-v1.jsonl"));
                available = true; failureReason = "None";
            }
            catch (Exception error) when (NonFatal(error)) { Fail("OpenFailed", error); }
        }
    }

    public ILogger CreateLogger(string categoryName) => new TrendLogger(this);

    public void RequireReady()
    {
        lock (gate)
            if (!available || disposed)
                throw new InvalidOperationException("Durable Google trend history is unavailable; inspect its fixed health code before another reservation.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; available = false;
            if (string.Equals(failureReason, "None", StringComparison.Ordinal)) failureReason = "Disposed";
            try { stream?.Dispose(); }
            catch (Exception error) when (NonFatal(error)) { Fail("CloseFailed", error); }
            stream = null;
        }
    }

    private void Capture<TState>(EventId id, TState state)
    {
        if (id.Id is not (>= 1600 and <= 1626) and not 6300) return;
        lock (gate)
        {
            if (!available || disposed) return;
            try
            {
                var safe = SafeDiagnosticMetadata.Capture(id, state);
                Append(new(1, clock.GetUtcNow(), "Event", id.Id, safe.EventName, safe.Component,
                    safe.Fields, safe.FieldsComplete, null));
            }
            catch (Exception error) when (NonFatal(error)) { Fail("WriteFailed", error); }
        }
    }

    private void Fail(string code, Exception error)
    {
        available = false; failureReason = code; failureHResult = error.HResult;
    }

    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private sealed class TrendLogger(GoogleTrendHistoryLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && owner.IsAvailable;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Capture(eventId, state);
        }
    }
}

using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    private static long _observerFailureCount;
    /// <summary>Process-local diagnostic loss signal. Contains no failure messages and grants no action authority.</summary>
    public static long ObserverFailureCount => Interlocked.Read(ref _observerFailureCount);

    public static bool ObserveIsEnabled(ILogger logger, LogLevel level)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try { return logger.IsEnabled(level); }
        catch (Exception error) when (NonFatalObserverFailure(error)) { RecordObserverFailure(); return false; }
    }

    /// <summary>For advisory logging only. Never put provider, persistence or approval operations inside this callback.</summary>
    public static void Observe(Action log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try { log(); }
        catch (Exception error) when (NonFatalObserverFailure(error)) { RecordObserverFailure(); }
    }

    private static bool NonFatalObserverFailure(Exception error) =>
        error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static void RecordObserverFailure()
    {
        long current;
        do { current = Interlocked.Read(ref _observerFailureCount); if (current == long.MaxValue) return; }
        while (Interlocked.CompareExchange(ref _observerFailureCount, current + 1, current) != current);
    }
}

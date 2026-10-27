using CloudScribe.Application.Startup;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Tests;

public sealed class StartupPhaseTimingsTests
{
    [Fact]
    public void EarlyPhasesBufferUntilLoggerAndFlushOnceWithMonotonicDurations()
    {
        ManualClock clock = new();
        StartupPhaseTimings timings = new(clock);
        CaptureLogger logger = new();
        timings.Start(StartupPhase.HostComposition);
        clock.Advance(125);
        timings.Complete(StartupPhase.HostComposition);
        Assert.Empty(logger.Events);
        timings.AttachLogger(logger);
        timings.AttachLogger(logger);
        Assert.Equal(2, logger.Events.Count);
        Assert.Contains("HostComposition Completed, phase 125 ms, managed startup 125 ms", logger.Events[1], StringComparison.Ordinal);
        Assert.Empty(timings.BufferedSummary());
    }

    [Fact]
    public void BackendBudgetRemainingAndDesktopHandoffAreObservationsOnly()
    {
        ManualClock clock = new();
        StartupPhaseTimings timings = new(clock);
        CaptureLogger logger = new();
        timings.AttachLogger(logger);
        timings.ArmBudget(20);
        timings.Start(StartupPhase.InitializerResolution);
        clock.Advance(14_000);
        timings.Complete(StartupPhase.InitializerResolution);
        Assert.Contains("backend budget Armed, remaining 6000 ms", logger.Events[^1], StringComparison.Ordinal);
        timings.DisarmBudget();
        timings.Start(StartupPhase.DesktopThreadScheduling);
        clock.Advance(30_000);
        timings.Complete(StartupPhase.DesktopThreadScheduling);
        Assert.Contains("backend budget Disarmed, remaining -1 ms", logger.Events[^1], StringComparison.Ordinal);
        Assert.Contains("phase 30000 ms", logger.Events[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedNestedPhaseAndExpiredBudgetRetainUsefulContext()
    {
        ManualClock clock = new();
        StartupPhaseTimings timings = new(clock);
        CaptureLogger logger = new();
        timings.AttachLogger(logger);
        timings.ArmBudget(20);
        timings.Start(StartupPhase.Readiness);
        timings.Start(StartupPhase.FrameworkInitialization);
        timings.Complete(StartupPhase.FrameworkInitialization);
        clock.Advance(21_000);
        timings.InterruptCurrent();
        timings.BudgetExpired();
        Assert.Contains("Readiness Interrupted, phase 21000 ms", logger.Events[^2], StringComparison.Ordinal);
        Assert.Contains("BackendBudget BudgetExpired, phase 21000 ms", logger.Events[^1], StringComparison.Ordinal);
        Assert.Contains("budget Expired, remaining 0 ms", logger.Events[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPhaseAndBufferedSummaryCannotCarryArbitraryUserData()
    {
        StartupPhaseTimings timings = new(new ManualClock());
        timings.Start((StartupPhase)999);
        timings.Complete((StartupPhase)999);
        string summary = timings.BufferedSummary();
        Assert.Contains("phase=Unknown", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("999", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("path", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", summary, StringComparison.OrdinalIgnoreCase);
        CaptureLogger logger = new();
        timings.AttachLogger(logger);
        Assert.All(logger.EventIds, id => Assert.Equal(1020, id));
    }

    [Fact]
    public void ConcurrentSignalsAndEarlyBufferStayWithinOneProcessRecordCap()
    {
        StartupPhaseTimings timings = new();
        Parallel.For(0, 200, _ => timings.Start(StartupPhase.CoreServiceResolution));
        Assert.InRange(timings.BufferedSummary().Length, 1, 16_384);
        CaptureLogger logger = new();
        timings.AttachLogger(logger);
        Parallel.For(0, 200, _ => timings.Complete(StartupPhase.CoreServiceResolution));
        Assert.Equal(StartupPhaseTimings.MaximumRecords, logger.Events.Count);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(long milliseconds) => _timestamp += milliseconds;
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly System.Threading.Lock _gate = new();
        public List<string> Events { get; } = [];
        public List<int> EventIds { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) { Events.Add(formatter(state, exception)); EventIds.Add(eventId.Id); }
        }
    }
}

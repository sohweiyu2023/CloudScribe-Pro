using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Tests;

public sealed class ActivityTimelineEntryTests
{
    [Fact]
    public void UsesInjectedClockAndRequiresBoundedTokens()
    {
        DateTimeOffset instant = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider clock = new(instant);

        ActivityTimelineEntry entry = ActivityTimelineEntry.Create(clock, ActivitySeverity.Information, "APP_READY", "Ready.", "correlation");

        Assert.Equal(instant, entry.OccurredAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => ActivityTimelineEntry.Create(clock, ActivitySeverity.Information, new string('x', 81), "Ready.", "correlation"));
    }

    [Fact]
    public void DirectConstructionCannotBypassDurableStateInvariants()
    {
        DateTimeOffset utc = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new ActivityTimelineEntry(Guid.Empty, utc, ActivitySeverity.Information, "EVENT", "Summary", "correlation"));
        Assert.Throws<ArgumentException>(() => new ActivityTimelineEntry(Guid.NewGuid(), utc.ToOffset(TimeSpan.FromHours(8)), ActivitySeverity.Information, "EVENT", "Summary", "correlation"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityTimelineEntry(Guid.NewGuid(), utc, (ActivitySeverity)999, "EVENT", "Summary", "correlation"));
    }

    private sealed class FakeTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}

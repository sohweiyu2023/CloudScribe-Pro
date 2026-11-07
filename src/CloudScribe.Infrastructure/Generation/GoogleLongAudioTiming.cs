using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Content-free local timing, never submission authority or billing evidence.</summary>
public sealed record GoogleLongAudioTiming(int Version, string RootBindingSha256, DateTimeOffset FirstObservedUtc,
    DateTimeOffset? FinishedUtc, long RecordedActiveMilliseconds, bool EarlierHistoryUnknown, bool ActiveTimeIncomplete,
    bool Running, LongAudioWorkflowStage LastStage)
{
    public TimeSpan ElapsedAt(DateTimeOffset now) => (FinishedUtc ?? now) >= FirstObservedUtc
        ? (FinishedUtc ?? now) - FirstObservedUtc : TimeSpan.Zero;
}

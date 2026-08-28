namespace CloudScribe.Domain.Generation;

public sealed record SpeechPause(TimeSpan Duration) : SpeechPlanNode
{
    public TimeSpan Duration { get; init; } = Duration >= TimeSpan.Zero && Duration <= TimeSpan.FromMinutes(5)
        ? Duration
        : throw new ArgumentOutOfRangeException(nameof(Duration), "Pause duration must be between zero and five minutes.");
}

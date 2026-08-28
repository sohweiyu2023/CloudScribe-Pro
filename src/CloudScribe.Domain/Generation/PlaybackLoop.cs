namespace CloudScribe.Domain.Generation;

public sealed record PlaybackLoop(TimeSpan Start, TimeSpan End)
{
    public PlaybackLoop Validate(TimeSpan duration)
    {
        if (Start < TimeSpan.Zero || End <= Start || End > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Playback loop must be a positive range within media duration.");
        }

        return this;
    }
}

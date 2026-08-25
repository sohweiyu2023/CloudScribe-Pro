namespace CloudScribe.Domain.Accessibility;

public sealed record AccessiblePlaybackSnapshot(
    bool IsPlaying,
    bool IsMuted,
    double Volume,
    double Speed,
    TimeSpan Position,
    TimeSpan Duration,
    string? CurrentChapter,
    string? CurrentSegmentLabel)
{
    public AccessiblePlaybackSnapshot Validate()
    {
        return Validate(Volume, Speed, Position, Duration, IsPlaying, IsMuted, CurrentChapter, CurrentSegmentLabel);
    }

    private static AccessiblePlaybackSnapshot Validate(
        double volume,
        double speed,
        TimeSpan position,
        TimeSpan duration,
        bool isPlaying,
        bool isMuted,
        string? currentChapter,
        string? currentSegmentLabel)
    {
        if (volume is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }
        if (speed is < 0.5 or > 3.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }
        if (position < TimeSpan.Zero || duration < TimeSpan.Zero || position > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        return new AccessiblePlaybackSnapshot(isPlaying, isMuted, volume, speed, position, duration, currentChapter, currentSegmentLabel);
    }
}

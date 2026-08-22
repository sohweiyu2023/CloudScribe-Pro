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
        if (Volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(Volume));
        if (Speed is < 0.5 or > 3.0) throw new ArgumentOutOfRangeException(nameof(Speed));
        if (Position < TimeSpan.Zero || Duration < TimeSpan.Zero || Position > Duration)
            throw new ArgumentOutOfRangeException(nameof(Position));
        return this;
    }
}

public sealed record AccessiblePlaybackAnnouncement(string PoliteText, string AssertiveText);

public static class AccessiblePlaybackAnnouncer
{
    public static AccessiblePlaybackAnnouncement DescribeTransition(
        AccessiblePlaybackSnapshot previous,
        AccessiblePlaybackSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        previous.Validate();
        current.Validate();

        var polite = new List<string>();
        var assertive = new List<string>();

        if (previous.IsPlaying != current.IsPlaying)
            polite.Add(current.IsPlaying ? "Playback started." : "Playback paused.");
        if (previous.IsMuted != current.IsMuted)
            polite.Add(current.IsMuted ? "Muted." : $"Unmuted, volume {Percent(current.Volume)} percent.");
        else if (Math.Abs(previous.Volume - current.Volume) >= 0.05)
            polite.Add($"Volume {Percent(current.Volume)} percent.");
        if (Math.Abs(previous.Speed - current.Speed) >= 0.01)
            polite.Add($"Playback speed {current.Speed:0.##} times.");
        if (!string.Equals(previous.CurrentChapter, current.CurrentChapter, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(current.CurrentChapter))
            assertive.Add($"Chapter: {current.CurrentChapter}.");
        if (!string.Equals(previous.CurrentSegmentLabel, current.CurrentSegmentLabel, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(current.CurrentSegmentLabel))
            polite.Add($"Segment: {current.CurrentSegmentLabel}.");

        return new AccessiblePlaybackAnnouncement(
            string.Join(" ", polite),
            string.Join(" ", assertive));
    }

    private static int Percent(double value) => (int)Math.Round(value * 100, MidpointRounding.AwayFromZero);
}

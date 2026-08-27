namespace CloudScribe.Domain.Generation;

public sealed record PlaybackQueueItem(
    string ItemId,
    string MediaPath,
    string DisplayTitle,
    TimeSpan Duration,
    TimeSpan ResumePosition,
    bool Missing,
    bool Corrupt)
{
    public PlaybackQueueItem Validate() => ValidateCore(
        ItemId,
        MediaPath,
        DisplayTitle,
        Duration,
        ResumePosition,
        Missing,
        Corrupt);

    private PlaybackQueueItem ValidateCore(
        string itemId,
        string mediaPath,
        string displayTitle,
        TimeSpan duration,
        TimeSpan resumePosition,
        bool missing,
        bool corrupt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayTitle);
        if (!Path.IsPathFullyQualified(mediaPath)) throw new InvalidOperationException("Playback media paths must be fully qualified.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        if (resumePosition < TimeSpan.Zero || resumePosition > duration) throw new ArgumentOutOfRangeException(nameof(resumePosition));
        if (missing && corrupt) throw new InvalidOperationException("Playback media cannot be both missing and corrupt.");
        return this;
    }
}

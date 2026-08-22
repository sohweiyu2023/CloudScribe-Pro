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
    public PlaybackQueueItem Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(MediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayTitle);
        if (!Path.IsPathFullyQualified(MediaPath)) throw new InvalidOperationException("Playback media paths must be fully qualified.");
        if (Duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Duration));
        if (ResumePosition < TimeSpan.Zero || ResumePosition > Duration) throw new ArgumentOutOfRangeException(nameof(ResumePosition));
        if (Missing && Corrupt) throw new InvalidOperationException("Playback media cannot be both missing and corrupt.");
        return this;
    }
}

public sealed record PlaybackQueueSnapshot(
    IReadOnlyList<PlaybackQueueItem> Items,
    int CurrentIndex,
    DateTimeOffset SavedAtUtc)
{
    public PlaybackQueueSnapshot Validate()
    {
        ArgumentNullException.ThrowIfNull(Items);
        if (Items.Count == 0)
        {
            if (CurrentIndex != -1) throw new InvalidOperationException("An empty playback queue must use current index -1.");
            return this;
        }
        if (CurrentIndex < 0 || CurrentIndex >= Items.Count) throw new ArgumentOutOfRangeException(nameof(CurrentIndex));
        var validated = Items.Select(static item => item.Validate()).ToArray();
        var duplicate = validated.GroupBy(static item => item.ItemId, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Playback queue contains duplicate item identity: {duplicate.Key}");
        return this with { Items = validated };
    }

    public PlaybackQueueItem? Current => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    public PlaybackQueueSnapshot MoveNextPlayable()
    {
        Validate();
        for (var index = CurrentIndex + 1; index < Items.Count; index++)
        {
            if (!Items[index].Missing && !Items[index].Corrupt) return this with { CurrentIndex = index };
        }
        return this;
    }

    public PlaybackQueueSnapshot RememberPosition(string itemId, TimeSpan position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var copy = Items.ToArray();
        var index = Array.FindIndex(copy, item => string.Equals(item.ItemId, itemId, StringComparison.Ordinal));
        if (index < 0) throw new KeyNotFoundException(itemId);
        if (position < TimeSpan.Zero || position > copy[index].Duration) throw new ArgumentOutOfRangeException(nameof(position));
        copy[index] = copy[index] with { ResumePosition = position };
        return (this with { Items = copy }).Validate();
    }
}

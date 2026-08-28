namespace CloudScribe.Domain.Generation;

public sealed record PlaybackQueueSnapshot(
    IReadOnlyList<PlaybackQueueItem> Items,
    int CurrentIndex,
    DateTimeOffset SavedAtUtc)
{
    public PlaybackQueueSnapshot Validate() => ValidateCore(Items, CurrentIndex);

    private PlaybackQueueSnapshot ValidateCore(IReadOnlyList<PlaybackQueueItem> items, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            if (currentIndex != -1) throw new InvalidOperationException("An empty playback queue must use current index -1.");
            return this;
        }

        if (currentIndex < 0 || currentIndex >= items.Count) throw new ArgumentOutOfRangeException(nameof(currentIndex));
        var validated = items.Select(static item => item.Validate()).ToArray();
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

using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5PlaybackQueueLibraryTests
{
    [Fact]
    public void QueueSkipsMissingAndCorruptItemsWithoutLosingOrder()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-queue"));
        var snapshot = new PlaybackQueueSnapshot(
            [
                Item("a", root, false, false),
                Item("b", root, true, false),
                Item("c", root, false, true),
                Item("d", root, false, false),
            ], 0, DateTimeOffset.UtcNow).Validate();

        var moved = snapshot.MoveNextPlayable();
        Assert.Equal(3, moved.CurrentIndex);
        Assert.Equal("d", moved.Current!.ItemId);
    }

    [Fact]
    public void ResumePositionIsBoundedAndRememberedByStableIdentity()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-queue"));
        var snapshot = new PlaybackQueueSnapshot([Item("a", root, false, false)], 0, DateTimeOffset.UtcNow).Validate();

        var updated = snapshot.RememberPosition("a", TimeSpan.FromSeconds(17));
        Assert.Equal(TimeSpan.FromSeconds(17), updated.Current!.ResumePosition);
        Assert.Throws<ArgumentOutOfRangeException>(() => updated.RememberPosition("a", TimeSpan.FromMinutes(2)));
    }

    private static PlaybackQueueItem Item(string id, string root, bool missing, bool corrupt) =>
        new(id, Path.Combine(root, id + ".wav"), id, TimeSpan.FromMinutes(1), TimeSpan.Zero, missing, corrupt);
}

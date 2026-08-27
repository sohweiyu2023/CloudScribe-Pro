using CloudScribe.Domain.Accessibility;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8AccessiblePlaybackStateTests
{
    [Fact]
    public void DescribeTransition_AnnouncesPlaybackChapterAndSpeedChanges()
    {
        var previous = new AccessiblePlaybackSnapshot(
            false, false, 0.5, 1.0, TimeSpan.Zero, TimeSpan.FromMinutes(10), "Intro", "Segment 1");
        var current = new AccessiblePlaybackSnapshot(
            true, false, 0.5, 1.25, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), "Chapter 1", "Segment 2");

        var result = AccessiblePlaybackAnnouncer.DescribeTransition(previous, current);

        Assert.Contains("Playback started.", result.PoliteText, StringComparison.Ordinal);
        Assert.Contains("Playback speed 1.25 times.", result.PoliteText, StringComparison.Ordinal);
        Assert.Contains("Segment: Segment 2.", result.PoliteText, StringComparison.Ordinal);
        Assert.Equal("Chapter: Chapter 1.", result.AssertiveText);
    }

    [Fact]
    public void Snapshot_RejectsPositionBeyondDuration()
    {
        var snapshot = new AccessiblePlaybackSnapshot(
            false, false, 0.5, 1.0, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), null, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Validate());
    }
}

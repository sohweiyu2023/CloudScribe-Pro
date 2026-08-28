using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5ReleasePlaybackAcceptanceTests
{
    [Fact]
    public void PublishedMediaFlowsThroughTimedTextQueuePlaybackBookmarksLoopAndSleepTimer()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage5-playback-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var releasePath = Path.Combine(root, "release.wav");
            File.WriteAllBytes(releasePath, CreateWave());
            AssertTimedTextExports();
            var queue = CreateAndAssertQueue(root, releasePath);
            AssertPlaybackSession(queue);
            AssertSleepTimer();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertTimedTextExports()
    {
        var timedText = new TimedTextTrack(
        [
            new TimedTextCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello Singapore.", "segment/provenance/1"),
            new TimedTextCue(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "CloudScribe playback acceptance.", "segment/provenance/2"),
        ]);
        var json = TimedTextExporter.Export(timedText, TimedTextExportFormat.Json);
        var webVtt = TimedTextExporter.Export(timedText, TimedTextExportFormat.WebVtt);
        var srt = TimedTextExporter.Export(timedText, TimedTextExportFormat.SubRip);

        Assert.Contains("cloudscribe.timed-text.v1", json, StringComparison.Ordinal);
        Assert.Contains("segment/provenance/1", json, StringComparison.Ordinal);
        Assert.StartsWith("WEBVTT", webVtt, StringComparison.Ordinal);
        Assert.Contains("NOTE provenance:segment/provenance/2", webVtt, StringComparison.Ordinal);
        Assert.Contains("[provenance:segment/provenance/1]", srt, StringComparison.Ordinal);
    }

    private static PlaybackQueueSnapshot CreateAndAssertQueue(string root, string releasePath)
    {
        var queue = new PlaybackQueueSnapshot(
        [
            new PlaybackQueueItem(
                "release-1", releasePath, "CloudScribe release", TimeSpan.FromSeconds(2), TimeSpan.Zero,
                Missing: false, Corrupt: false),
            new PlaybackQueueItem(
                "missing-1", Path.Combine(root, "missing.wav"), "Unavailable release",
                TimeSpan.FromSeconds(2), TimeSpan.Zero, Missing: true, Corrupt: false),
        ],
        CurrentIndex: 0,
        SavedAtUtc: DateTimeOffset.UtcNow).Validate();

        Assert.Equal(releasePath, queue.Current!.MediaPath);
        Assert.Equal(0, queue.MoveNextPlayable().CurrentIndex);
        queue = queue.RememberPosition("release-1", TimeSpan.FromMilliseconds(750));
        Assert.Equal(TimeSpan.FromMilliseconds(750), queue.Current!.ResumePosition);
        return queue;
    }

    private static void AssertPlaybackSession(PlaybackQueueSnapshot queue)
    {
        var playback = new PlaybackSession(queue.Current!.Duration);
        playback.Seek(queue.Current.ResumePosition);
        playback.SetVolume(0.65);
        playback.SetSpeed(1.25);
        playback.SetLoop(new PlaybackLoop(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)));
        playback.AddBookmark(new PlaybackBookmark("Proof point", TimeSpan.FromSeconds(1)));
        playback.Play();

        Assert.Equal(PlaybackState.Playing, playback.State);
        Assert.Equal(TimeSpan.FromMilliseconds(750), playback.Position);
        Assert.Equal(0.65, playback.Volume);
        Assert.Equal(1.25, playback.Speed);
        Assert.Single(playback.Bookmarks);
        Assert.NotNull(playback.Loop);

        playback.MarkCorruptMedia();
        Assert.Throws<InvalidOperationException>(playback.Play);
        playback.RestoreMedia();
        playback.Play();
        Assert.Equal(PlaybackState.Playing, playback.State);
    }

    private static void AssertSleepTimer()
    {
        var clock = new ManualPlaybackTimeProvider();
        var sleepTimer = new PlaybackSleepTimer(clock);
        sleepTimer.Arm(TimeSpan.FromMinutes(20));
        Assert.True(sleepTimer.IsArmed);
        Assert.False(sleepTimer.ShouldStopPlayback());
        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.InRange(sleepTimer.Remaining(), TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(61));
        Assert.False(sleepTimer.ShouldStopPlayback());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(sleepTimer.ShouldStopPlayback());
    }

    private static byte[] CreateWave()
    {
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.GetBytes(36).CopyTo(bytes, 4);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(16_000).CopyTo(bytes, 24);
        BitConverter.GetBytes(32_000).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.GetBytes(0).CopyTo(bytes, 40);
        return bytes;
    }

    private sealed class ManualPlaybackTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_timestamp);

        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            _timestamp = checked(_timestamp + duration.Ticks);
        }
    }
}

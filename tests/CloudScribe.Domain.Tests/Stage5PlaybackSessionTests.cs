using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5PlaybackSessionTests
{
    [Fact]
    public void PlayerSupportsSeekSkipSpeedVolumeLoopAndBookmarks()
    {
        var session = new PlaybackSession(TimeSpan.FromMinutes(10));

        session.Seek(TimeSpan.FromMinutes(2));
        session.Skip(TimeSpan.FromSeconds(30));
        session.SetVolume(0.4);
        session.SetSpeed(1.5);
        session.SetLoop(new PlaybackLoop(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)));
        session.AddBookmark(new PlaybackBookmark("chapter", TimeSpan.FromMinutes(4)));
        session.AddBookmark(new PlaybackBookmark("intro", TimeSpan.FromSeconds(15)));
        session.Play();

        Assert.Equal(TimeSpan.FromMinutes(2.5), session.Position);
        Assert.Equal(0.4, session.Volume);
        Assert.Equal(1.5, session.Speed);
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.NotNull(session.Loop);
        Assert.Equal("intro", session.Bookmarks[0].Label);
        Assert.Equal("chapter", session.Bookmarks[1].Label);
    }

    [Fact]
    public void MissingOrCorruptMediaFailsClosedUntilExplicitlyRestored()
    {
        var session = new PlaybackSession(TimeSpan.FromMinutes(1));
        session.MarkMissingMedia();
        Assert.Throws<InvalidOperationException>(session.Play);

        session.RestoreMedia();
        session.Play();
        Assert.Equal(PlaybackState.Playing, session.State);

        session.MarkCorruptMedia();
        Assert.Throws<InvalidOperationException>(() => session.Seek(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SkipClampsToMediaBounds()
    {
        var session = new PlaybackSession(TimeSpan.FromSeconds(30));
        session.Skip(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromSeconds(30), session.Position);

        session.Skip(TimeSpan.FromMinutes(-2));
        Assert.Equal(TimeSpan.Zero, session.Position);
    }

    [Fact]
    public void SleepTimerUsesInjectedMonotonicTimestampRatherThanWallClock()
    {
        var time = new ManualTimeProvider();
        var timer = new PlaybackSleepTimer(time);

        timer.Arm(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(12));
        Assert.InRange(timer.Remaining(), TimeSpan.FromSeconds(17.9), TimeSpan.FromSeconds(18.1));
        Assert.False(timer.ShouldStopPlayback());

        time.Advance(TimeSpan.FromSeconds(18));
        Assert.Equal(TimeSpan.Zero, timer.Remaining());
        Assert.True(timer.ShouldStopPlayback());
    }

    [Fact]
    public void SleepTimerCanBeCancelledAndIsBounded()
    {
        var timer = new PlaybackSleepTimer(new ManualTimeProvider());
        timer.Arm(TimeSpan.FromMinutes(5));
        timer.Cancel();
        Assert.False(timer.IsArmed);
        Assert.False(timer.ShouldStopPlayback());
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Arm(TimeSpan.FromHours(25)));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _timestamp = checked(_timestamp + (long)(duration.TotalSeconds * TimestampFrequency));
        }
    }
}

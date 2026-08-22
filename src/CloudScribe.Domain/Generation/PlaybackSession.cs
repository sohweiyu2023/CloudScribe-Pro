namespace CloudScribe.Domain.Generation;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    MissingMedia,
    CorruptMedia,
}

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

public sealed record PlaybackBookmark(string Label, TimeSpan Position)
{
    public PlaybackBookmark Validate(TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Label);
        if (Position < TimeSpan.Zero || Position > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(Position));
        }

        return this;
    }
}

public sealed class PlaybackSession
{
    private readonly List<PlaybackBookmark> _bookmarks = [];

    public PlaybackSession(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public TimeSpan Position { get; private set; }

    public double Volume { get; private set; } = 1.0;

    public double Speed { get; private set; } = 1.0;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public PlaybackLoop? Loop { get; private set; }

    public IReadOnlyList<PlaybackBookmark> Bookmarks => _bookmarks;

    public void Play()
    {
        EnsurePlayable();
        State = PlaybackState.Playing;
    }

    public void Pause()
    {
        if (State == PlaybackState.Playing)
        {
            State = PlaybackState.Paused;
        }
    }

    public void Seek(TimeSpan position)
    {
        EnsurePlayable();
        if (position < TimeSpan.Zero || position > Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        Position = position;
    }

    public void Skip(TimeSpan delta)
    {
        var target = Position + delta;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }
        else if (target > Duration)
        {
            target = Duration;
        }

        Seek(target);
    }

    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        Volume = volume;
    }

    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed is < 0.5 or > 3.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        Speed = speed;
    }

    public void SetLoop(PlaybackLoop? loop)
    {
        Loop = loop?.Validate(Duration);
    }

    public void AddBookmark(PlaybackBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        var validated = bookmark.Validate(Duration);
        if (_bookmarks.Any(existing =>
            existing.Position == validated.Position && string.Equals(existing.Label, validated.Label, StringComparison.Ordinal)))
        {
            return;
        }

        _bookmarks.Add(validated);
        _bookmarks.Sort(static (left, right) => left.Position.CompareTo(right.Position));
    }

    public void MarkMissingMedia()
    {
        State = PlaybackState.MissingMedia;
    }

    public void MarkCorruptMedia()
    {
        State = PlaybackState.CorruptMedia;
    }

    public void RestoreMedia()
    {
        if (State is PlaybackState.MissingMedia or PlaybackState.CorruptMedia)
        {
            State = PlaybackState.Paused;
        }
    }

    private void EnsurePlayable()
    {
        if (State is PlaybackState.MissingMedia or PlaybackState.CorruptMedia)
        {
            throw new InvalidOperationException("Playback cannot proceed while media is missing or corrupt.");
        }
    }
}

public sealed class PlaybackSleepTimer
{
    private readonly TimeProvider _timeProvider;
    private long? _deadlineTimestamp;

    public PlaybackSleepTimer(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool IsArmed => _deadlineTimestamp.HasValue;

    public void Arm(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var delta = (long)Math.Ceiling(duration.TotalSeconds * _timeProvider.TimestampFrequency);
        _deadlineTimestamp = checked(_timeProvider.GetTimestamp() + delta);
    }

    public void Cancel() => _deadlineTimestamp = null;

    public TimeSpan Remaining()
    {
        if (_deadlineTimestamp is not { } deadline)
        {
            return TimeSpan.Zero;
        }

        var now = _timeProvider.GetTimestamp();
        if (now >= deadline)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds((deadline - now) / (double)_timeProvider.TimestampFrequency);
    }

    public bool ShouldStopPlayback() => IsArmed && Remaining() == TimeSpan.Zero;
}

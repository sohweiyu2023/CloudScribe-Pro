namespace CloudScribe.Domain.Generation;

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

namespace CloudScribe.Domain.Generation;

public sealed class GenerationCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private int _consecutiveFailures;
    private long? _openedAtTimestamp;

    public GenerationCircuitBreaker(int failureThreshold, TimeSpan cooldown, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        if (cooldown <= TimeSpan.Zero || cooldown > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _failureThreshold = failureThreshold;
        _cooldown = cooldown;
    }

    public bool IsOpen
    {
        get
        {
            if (_openedAtTimestamp is not { } openedAt)
            {
                return false;
            }

            return _timeProvider.GetElapsedTime(openedAt, _timeProvider.GetTimestamp()) < _cooldown;
        }
    }

    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _openedAtTimestamp = null;
    }

    public void RecordFailure()
    {
        if (_consecutiveFailures < int.MaxValue)
        {
            _consecutiveFailures++;
        }

        if (_consecutiveFailures >= _failureThreshold && _openedAtTimestamp is null)
        {
            _openedAtTimestamp = _timeProvider.GetTimestamp();
        }
    }

    public bool MayAttempt()
    {
        if (_openedAtTimestamp is not { } openedAt)
        {
            return true;
        }

        if (_timeProvider.GetElapsedTime(openedAt, _timeProvider.GetTimestamp()) < _cooldown)
        {
            return false;
        }

        _openedAtTimestamp = null;
        _consecutiveFailures = 0;
        return true;
    }
}

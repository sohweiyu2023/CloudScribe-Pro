namespace CloudScribe.Domain.Generation;

public sealed class GenerationConcurrencyGate
{
    private readonly int _maximumConcurrent;
    private int _active;

    public GenerationConcurrencyGate(int maximumConcurrent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrent, 1);
        _maximumConcurrent = maximumConcurrent;
    }

    public int ActiveCount => Volatile.Read(ref _active);

    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= _maximumConcurrent)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _active);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _active, 0);
            throw new InvalidOperationException("Generation concurrency gate released without a matching acquisition.");
        }
    }
}

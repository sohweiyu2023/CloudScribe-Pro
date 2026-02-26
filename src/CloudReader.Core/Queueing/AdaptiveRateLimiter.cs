using System.Threading.RateLimiting;

namespace CloudReader.Core.Queueing;

public sealed class AdaptiveRateLimiter
{
    private int _concurrency;
    private readonly int _min;

    public AdaptiveRateLimiter(int initialConcurrency = 4, int minConcurrency = 1)
    {
        _concurrency = initialConcurrency;
        _min = minConcurrency;
    }

    public ConcurrencyLimiter BuildLimiter() => new(new ConcurrencyLimiterOptions
    {
        PermitLimit = _concurrency,
        QueueLimit = 100,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

    public void RegisterThrottleSignal()
    {
        _concurrency = Math.Max(_min, _concurrency - 1);
    }
}

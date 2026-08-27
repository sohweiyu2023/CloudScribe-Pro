using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7ProviderRouteHealthCircuitTests
{
    [Fact]
    public void TransientFailuresOpenOnlyTheExactPinnedRoute()
    {
        var now = DateTimeOffset.UtcNow;
        var route = Route("voice-a");
        var circuit = ProviderRouteHealthCircuit.Start(route, now)
            .RecordTransientFailure(route, now.AddSeconds(1), 2, TimeSpan.FromMinutes(1))
            .RecordTransientFailure(route, now.AddSeconds(2), 2, TimeSpan.FromMinutes(1));

        Assert.Equal(ProviderRouteHealthState.CircuitOpen, circuit.State);
        Assert.False(circuit.CanAttempt(now.AddSeconds(30)));
        Assert.True(circuit.CanAttempt(now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => circuit.EnsureAttemptAllowed(Route("voice-b"), now.AddMinutes(2)));
    }

    [Fact]
    public void RateLimitHonorsExactRetryAfterWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var route = Route("voice-a");
        var circuit = ProviderRouteHealthCircuit.Start(route, now)
            .RecordRateLimit(route, now.AddSeconds(1), TimeSpan.FromSeconds(45));

        Assert.Equal(ProviderRouteHealthState.RateLimited, circuit.State);
        Assert.False(circuit.CanAttempt(now.AddSeconds(45)));
        Assert.True(circuit.CanAttempt(now.AddSeconds(46)));
    }

    [Fact]
    public void SuccessResetsFailuresAndCircuitWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var route = Route("voice-a");
        var circuit = ProviderRouteHealthCircuit.Start(route, now)
            .RecordTransientFailure(route, now.AddSeconds(1), 1, TimeSpan.FromMinutes(5))
            .RecordSuccess(route, now.AddMinutes(6));

        Assert.Equal(ProviderRouteHealthState.Healthy, circuit.State);
        Assert.Equal(0, circuit.ConsecutiveFailures);
        Assert.Null(circuit.RetryNotBeforeUtc);
        Assert.True(circuit.CanAttempt(now.AddMinutes(6)));
    }

    private static ProviderRouteHealthKey Route(string voice) => new(
        "provider-a",
        "account-a",
        "synthesize",
        voice);
}

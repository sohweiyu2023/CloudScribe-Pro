namespace CloudScribe.Domain.Generation;

public enum ProviderRouteHealthState
{
    Healthy = 0,
    Degraded = 1,
    RateLimited = 2,
    CircuitOpen = 3,
}

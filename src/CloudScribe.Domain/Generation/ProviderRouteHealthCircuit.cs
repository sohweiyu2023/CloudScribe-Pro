namespace CloudScribe.Domain.Generation;

public sealed record ProviderRouteHealthKey(
    string ProviderStableId,
    string AccountId,
    string OperationStableId,
    string VoiceStableId)
{
    public ProviderRouteHealthKey Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceStableId);
        return this;
    }

    public string StableIdentity => string.Join("/", ProviderStableId, AccountId, OperationStableId, VoiceStableId);
}

public enum ProviderRouteHealthState
{
    Healthy = 0,
    Degraded = 1,
    RateLimited = 2,
    CircuitOpen = 3,
}

public sealed record ProviderRouteHealthCircuit(
    ProviderRouteHealthKey Route,
    ProviderRouteHealthState State,
    int ConsecutiveFailures,
    DateTimeOffset? RetryNotBeforeUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static ProviderRouteHealthCircuit Start(ProviderRouteHealthKey route, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(route);
        route.Validate();
        RequireTimestamp(nowUtc, nameof(nowUtc));
        return new ProviderRouteHealthCircuit(
            route,
            ProviderRouteHealthState.Healthy,
            0,
            null,
            nowUtc.ToUniversalTime());
    }

    public bool CanAttempt(DateTimeOffset nowUtc)
    {
        EnsureTime(nowUtc);
        return State switch
        {
            ProviderRouteHealthState.Healthy => true,
            ProviderRouteHealthState.Degraded => true,
            ProviderRouteHealthState.RateLimited or ProviderRouteHealthState.CircuitOpen =>
                RetryNotBeforeUtc is { } retry && nowUtc.ToUniversalTime() >= retry,
            _ => false,
        };
    }

    public void EnsureAttemptAllowed(ProviderRouteHealthKey route, DateTimeOffset nowUtc)
    {
        EnsureRoute(route);
        if (!CanAttempt(nowUtc))
            throw new InvalidOperationException($"Provider route '{Route.StableIdentity}' is circuit-blocked until {RetryNotBeforeUtc:O}.");
    }

    public ProviderRouteHealthCircuit RecordSuccess(ProviderRouteHealthKey route, DateTimeOffset nowUtc)
    {
        EnsureRoute(route);
        EnsureForwardTime(nowUtc);
        return this with
        {
            State = ProviderRouteHealthState.Healthy,
            ConsecutiveFailures = 0,
            RetryNotBeforeUtc = null,
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public ProviderRouteHealthCircuit RecordTransientFailure(
        ProviderRouteHealthKey route,
        DateTimeOffset nowUtc,
        int failureThreshold,
        TimeSpan circuitOpenDuration)
    {
        EnsureRoute(route);
        EnsureForwardTime(nowUtc);
        if (failureThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        ValidateDelay(circuitOpenDuration, nameof(circuitOpenDuration));

        var failures = checked(ConsecutiveFailures + 1);
        var opens = failures >= failureThreshold;
        return this with
        {
            State = opens ? ProviderRouteHealthState.CircuitOpen : ProviderRouteHealthState.Degraded,
            ConsecutiveFailures = failures,
            RetryNotBeforeUtc = opens ? nowUtc.ToUniversalTime().Add(circuitOpenDuration) : null,
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public ProviderRouteHealthCircuit RecordRateLimit(
        ProviderRouteHealthKey route,
        DateTimeOffset nowUtc,
        TimeSpan retryAfter)
    {
        EnsureRoute(route);
        EnsureForwardTime(nowUtc);
        ValidateDelay(retryAfter, nameof(retryAfter));
        return this with
        {
            State = ProviderRouteHealthState.RateLimited,
            RetryNotBeforeUtc = nowUtc.ToUniversalTime().Add(retryAfter),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    private void EnsureRoute(ProviderRouteHealthKey route)
    {
        ArgumentNullException.ThrowIfNull(route);
        route.Validate();
        if (route != Route)
            throw new InvalidOperationException("Provider route health cannot be applied to a different provider/account/operation/voice identity.");
    }

    private void EnsureTime(DateTimeOffset nowUtc) => RequireTimestamp(nowUtc, nameof(nowUtc));

    private void EnsureForwardTime(DateTimeOffset nowUtc)
    {
        RequireTimestamp(nowUtc, nameof(nowUtc));
        if (nowUtc.ToUniversalTime() < UpdatedAtUtc)
            throw new InvalidOperationException("Provider route health time cannot move backwards.");
    }

    private static void RequireTimestamp(DateTimeOffset value, string name)
    {
        if (value == default) throw new ArgumentException("Timestamp is required.", name);
    }

    private static void ValidateDelay(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(name, "Provider retry/circuit delay must be positive and no greater than 24 hours.");
    }
}

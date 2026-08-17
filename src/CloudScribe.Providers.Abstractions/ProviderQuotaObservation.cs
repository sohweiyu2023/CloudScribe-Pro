namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderQuotaObservation
{
    public ProviderQuotaObservation(
        ProviderAccountReference account,
        string limitId,
        string scopeId,
        string unitId,
        ProviderQuotaObservationState state,
        long? observedValue,
        long? limitValue,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? expiresAtUtc,
        string provenanceId,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Quota observation timestamps must be UTC.", nameof(observedAtUtc));
        }
        if (expiresAtUtc is { } expiry && expiry.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Quota expiry timestamps must be UTC.", nameof(expiresAtUtc));
        }
        if (expiresAtUtc is not null && expiresAtUtc.Value < observedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Quota evidence cannot expire before it was observed.");
        }
        if (observedValue is < 0 || limitValue is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedValue), "Quota values must be non-negative exact integers.");
        }
        if (state == ProviderQuotaObservationState.Unknown && (observedValue is not null || limitValue is not null))
        {
            throw new ArgumentException("Unknown quota state cannot pretend to carry observed values.", nameof(observedValue));
        }
        if (state != ProviderQuotaObservationState.Unknown && observedValue is null && limitValue is null)
        {
            throw new ArgumentException("Observed or conflicting quota state requires at least one measured value.", nameof(observedValue));
        }

        Account = account;
        LimitId = ProviderIdentifierRules.NormalizeStableId(limitId, nameof(limitId), 96);
        ScopeId = ProviderIdentifierRules.NormalizeStableId(scopeId, nameof(scopeId), 96);
        UnitId = ProviderIdentifierRules.NormalizeStableId(unitId, nameof(unitId), 64);
        State = state;
        ObservedValue = observedValue;
        LimitValue = limitValue;
        ObservedAtUtc = observedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ProvenanceId = ProviderIdentifierRules.NormalizeDisplayName(provenanceId, nameof(provenanceId), 160);
        Reason = ProviderIdentifierRules.NormalizeDisplayName(reason, nameof(reason), 256);
    }

    public ProviderAccountReference Account { get; }
    public string LimitId { get; }
    public string ScopeId { get; }
    public string UnitId { get; }
    public ProviderQuotaObservationState State { get; }
    public long? ObservedValue { get; }
    public long? LimitValue { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    public string ProvenanceId { get; }
    public string Reason { get; }

    public bool IsStale(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Quota staleness checks require UTC.", nameof(nowUtc));
        }

        return ExpiresAtUtc is not null && nowUtc >= ExpiresAtUtc.Value;
    }
}

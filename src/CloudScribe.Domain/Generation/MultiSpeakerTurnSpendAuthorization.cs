namespace CloudScribe.Domain.Generation;

public sealed record MultiSpeakerTurnSpendAuthorization(
    string SpeakerRole,
    string RouteIdentity,
    string PricingProvenanceId,
    string Currency,
    int Scale,
    long MaximumScaledAmount)
{
    public MultiSpeakerTurnSpendAuthorization Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SpeakerRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(RouteIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Currency);
        if (Scale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(Scale));
        if (MaximumScaledAmount < 0) throw new ArgumentOutOfRangeException(nameof(MaximumScaledAmount));
        return this;
    }

    public void EnsureAuthorized(SpeakerVoiceBinding binding, string currency, int scale, long projectedScaledAmount)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (!string.Equals(SpeakerRole, binding.SpeakerRole, StringComparison.Ordinal) ||
            !string.Equals(RouteIdentity, binding.RouteIdentity, StringComparison.Ordinal) ||
            !string.Equals(PricingProvenanceId, binding.PricingProvenanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Multi-speaker spend authorization no longer matches the pinned provider route and pricing provenance.");
        if (!string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase) || Scale != scale)
            throw new InvalidOperationException("Projected multi-speaker spend uses a different provider-billed currency or scale.");
        if (projectedScaledAmount < 0 || projectedScaledAmount > MaximumScaledAmount)
            throw new InvalidOperationException("Projected multi-speaker turn spend exceeds its explicit authorization ceiling.");
    }
}

public sealed record MultiSpeakerTurnSpendAuthorizationSet(IReadOnlyList<MultiSpeakerTurnSpendAuthorization> Items)
{
    public MultiSpeakerTurnSpendAuthorizationSet Validate(MultiSpeakerVoiceMap voiceMap)
    {
        ArgumentNullException.ThrowIfNull(Items);
        ArgumentNullException.ThrowIfNull(voiceMap);
        voiceMap.Validate();
        if (Items.Count == 0) throw new InvalidOperationException("At least one multi-speaker spend authorization is required.");
        var validated = Items.Select(static x => x.Validate()).ToArray();
        if (validated.Select(static x => x.SpeakerRole).Distinct(StringComparer.Ordinal).Count() != validated.Length)
            throw new InvalidOperationException("A speaker role cannot have multiple active spend authorizations.");
        var expected = voiceMap.Bindings.Select(static x => x.SpeakerRole).ToHashSet(StringComparer.Ordinal);
        var actual = validated.Select(static x => x.SpeakerRole).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new InvalidOperationException("Spend authorizations must cover exactly the pinned multi-speaker voice map.");
        foreach (var authorization in validated)
        {
            var binding = voiceMap.Resolve(authorization.SpeakerRole);
            if (!string.Equals(authorization.RouteIdentity, binding.RouteIdentity, StringComparison.Ordinal) ||
                !string.Equals(authorization.PricingProvenanceId, binding.PricingProvenanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Spend authorization is stale for the current pinned speaker route.");
        }
        return this with { Items = validated };
    }
}

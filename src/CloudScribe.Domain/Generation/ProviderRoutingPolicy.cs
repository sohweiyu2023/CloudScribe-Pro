namespace CloudScribe.Domain.Generation;

public sealed class ProviderRoutingPolicy
{
    private readonly StringComparer _stableIdComparer = StringComparer.Ordinal;

    public ProviderRoutingDecision Select(
        ProviderRoute requested,
        IReadOnlyList<ProviderRoute> candidates,
        bool allowFallback,
        long authorizedMaximumMinorUnits,
        string authorizedCurrency)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(candidates);
        requested.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedCurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(authorizedMaximumMinorUnits);

        var exact = candidates.Where(candidate => IsSameRoute(requested, candidate.Validate())).ToArray();
        if (exact.Length == 1)
        {
            ValidateSpend(exact[0], authorizedMaximumMinorUnits, authorizedCurrency);
            return new ProviderRoutingDecision(exact[0], false, "Exact pinned route selected.");
        }
        if (exact.Length > 1)
        {
            throw new InvalidOperationException("Ambiguous duplicate exact provider routes are not routable.");
        }
        if (!allowFallback)
        {
            throw new InvalidOperationException("Pinned provider route is unavailable and fallback is not authorized.");
        }

        var compatible = candidates
            .Select(static route => route.Validate())
            .Where(route => string.Equals(route.Currency, authorizedCurrency, StringComparison.OrdinalIgnoreCase))
            .Where(route => route.EstimatedMinorUnits <= authorizedMaximumMinorUnits)
            .OrderBy(route => route.EstimatedMinorUnits)
            .ThenBy(route => route.ProviderStableId, _stableIdComparer)
            .ThenBy(route => route.AccountId, _stableIdComparer)
            .ThenBy(route => route.OperationStableId, _stableIdComparer)
            .ThenBy(route => route.VoiceStableId, _stableIdComparer)
            .ToArray();

        if (compatible.Length == 0)
        {
            throw new InvalidOperationException("No explicitly authorized fallback route satisfies currency and spend constraints.");
        }

        var selected = compatible[0];
        if (!string.Equals(selected.PricingProvenanceId, requested.PricingProvenanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fallback would change pricing provenance and requires a new estimate/approval.");
        }

        return new ProviderRoutingDecision(selected, true, "Explicit fallback selected deterministically within spend and pricing-provenance constraints.");
    }

    private static bool IsSameRoute(ProviderRoute left, ProviderRoute right) =>
        string.Equals(left.ProviderStableId, right.ProviderStableId, StringComparison.Ordinal) &&
        string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal) &&
        string.Equals(left.OperationStableId, right.OperationStableId, StringComparison.Ordinal) &&
        string.Equals(left.VoiceStableId, right.VoiceStableId, StringComparison.Ordinal) &&
        string.Equals(left.PricingProvenanceId, right.PricingProvenanceId, StringComparison.Ordinal);

    private static void ValidateSpend(ProviderRoute route, long maximum, string currency)
    {
        if (!string.Equals(route.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pinned route currency does not match authorized currency.");
        }
        if (route.EstimatedMinorUnits > maximum)
        {
            throw new InvalidOperationException("Pinned route exceeds authorized spend ceiling.");
        }
    }
}

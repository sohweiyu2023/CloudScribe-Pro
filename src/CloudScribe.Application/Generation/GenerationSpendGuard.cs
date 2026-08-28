using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class GenerationSpendGuard
{
    private readonly StringComparer _provenanceComparer;

    public GenerationSpendGuard(StringComparer? provenanceComparer = null)
    {
        _provenanceComparer = provenanceComparer ?? StringComparer.Ordinal;
    }

    public void EnsureCollectionAuthorized(
        GenerationSpendAuthorization authorization,
        AuthorizedSpendCeiling projectedSpend,
        long currentRevision,
        string pricingProvenanceId)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        authorization.Validate();
        projectedSpend.Validate();

        if (currentRevision != authorization.ApprovedRevision ||
            !_provenanceComparer.Equals(pricingProvenanceId, authorization.PricingProvenanceId) ||
            !authorization.AllowsCollectionSpend(projectedSpend, currentRevision, pricingProvenanceId))
        {
            throw new InvalidOperationException("Projected collection spend is not covered by the exact current authorization.");
        }
    }

    public void EnsureItemAuthorized(
        GenerationSpendAuthorization authorization,
        Guid itemId,
        AuthorizedSpendCeiling projectedSpend,
        long currentRevision,
        string pricingProvenanceId)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        authorization.Validate();
        projectedSpend.Validate();

        if (currentRevision != authorization.ApprovedRevision ||
            !_provenanceComparer.Equals(pricingProvenanceId, authorization.PricingProvenanceId) ||
            !authorization.ItemCeilings.TryGetValue(itemId, out var ceiling) ||
            !ceiling.Allows(projectedSpend))
        {
            throw new InvalidOperationException("Projected item spend is not covered by the exact current authorization.");
        }
    }
}

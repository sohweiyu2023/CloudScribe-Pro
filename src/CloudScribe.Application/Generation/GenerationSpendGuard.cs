using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class GenerationSpendGuard
{
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

        if (!authorization.AllowsCollectionSpend(projectedSpend, currentRevision, pricingProvenanceId))
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
            !string.Equals(pricingProvenanceId, authorization.PricingProvenanceId, StringComparison.Ordinal) ||
            !authorization.ItemCeilings.TryGetValue(itemId, out var ceiling) ||
            !ceiling.Allows(projectedSpend))
        {
            throw new InvalidOperationException("Projected item spend is not covered by the exact current authorization.");
        }
    }
}

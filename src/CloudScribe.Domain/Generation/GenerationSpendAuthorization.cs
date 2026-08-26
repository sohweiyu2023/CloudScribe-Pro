namespace CloudScribe.Domain.Generation;

public sealed record GenerationSpendAuthorization(
    Guid CollectionId,
    AuthorizedSpendCeiling CollectionCeiling,
    IReadOnlyDictionary<Guid, AuthorizedSpendCeiling> ItemCeilings,
    string PricingProvenanceId,
    long ApprovedRevision)
{
    public void Validate()
    {
        Validate(CollectionId, CollectionCeiling, ItemCeilings, PricingProvenanceId, ApprovedRevision);
    }

    private static void Validate(
        Guid collectionId,
        AuthorizedSpendCeiling collectionCeiling,
        IReadOnlyDictionary<Guid, AuthorizedSpendCeiling> itemCeilings,
        string pricingProvenanceId,
        long approvedRevision)
    {
        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("Collection id is required.", nameof(collectionId));
        }

        collectionCeiling.Validate();
        ArgumentNullException.ThrowIfNull(itemCeilings);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(approvedRevision);

        foreach (var pair in itemCeilings)
        {
            if (pair.Key == Guid.Empty)
            {
                throw new ArgumentException("Spend authorization item ids must be non-empty.", nameof(itemCeilings));
            }

            pair.Value.Validate();
            if (!string.Equals(pair.Value.CurrencyCode, collectionCeiling.CurrencyCode, StringComparison.Ordinal) ||
                pair.Value.Scale != collectionCeiling.Scale)
            {
                throw new ArgumentException("Item and collection spend ceilings must use one exact currency and scale.", nameof(itemCeilings));
            }
        }
    }

    public bool AllowsCollectionSpend(AuthorizedSpendCeiling actual, long currentRevision, string pricingProvenanceId)
    {
        Validate();
        return currentRevision == ApprovedRevision &&
            string.Equals(PricingProvenanceId, pricingProvenanceId, StringComparison.Ordinal) &&
            CollectionCeiling.Allows(actual);
    }
}

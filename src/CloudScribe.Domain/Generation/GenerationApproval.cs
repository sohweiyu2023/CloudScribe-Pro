namespace CloudScribe.Domain.Generation;

public sealed record GenerationApproval(
    Guid CollectionId,
    int RequestRevision,
    string PricingProvenanceId,
    string Currency,
    long AuthorizedScaledAmount,
    int Scale,
    DateTimeOffset ApprovedAtUtc)
{
    public Guid CollectionId { get; init; } = CollectionId != Guid.Empty
        ? CollectionId
        : throw new ArgumentException("Collection id cannot be empty.", nameof(CollectionId));

    public int RequestRevision { get; init; } = RequestRevision >= 0
        ? RequestRevision
        : throw new ArgumentOutOfRangeException(nameof(RequestRevision));

    public string PricingProvenanceId { get; init; } = Require(PricingProvenanceId, nameof(PricingProvenanceId));

    public string Currency { get; init; } = Require(Currency, nameof(Currency)).ToUpperInvariant();

    public long AuthorizedScaledAmount { get; init; } = AuthorizedScaledAmount >= 0
        ? AuthorizedScaledAmount
        : throw new ArgumentOutOfRangeException(nameof(AuthorizedScaledAmount));

    public int Scale { get; init; } = Scale is >= 0 and <= 12
        ? Scale
        : throw new ArgumentOutOfRangeException(nameof(Scale));

    public DateTimeOffset ApprovedAtUtc { get; init; } = ApprovedAtUtc.ToUniversalTime();

    public bool Authorizes(GenerationCollectionEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.CollectionId == CollectionId
            && estimate.RequestRevision == RequestRevision
            && string.Equals(estimate.PricingProvenanceId, PricingProvenanceId, StringComparison.Ordinal)
            && string.Equals(estimate.Currency, Currency, StringComparison.Ordinal)
            && estimate.Scale == Scale
            && estimate.ScaledTotal <= AuthorizedScaledAmount;
    }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

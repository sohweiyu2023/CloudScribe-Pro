using System.Collections.ObjectModel;

namespace CloudScribe.Domain.Generation;

public sealed class GenerationCollectionEstimate
{
    public GenerationCollectionEstimate(
        Guid collectionId,
        int requestRevision,
        DateTimeOffset estimatedAtUtc,
        string currency,
        long scaledTotal,
        int scale,
        string pricingProvenanceId,
        IEnumerable<GenerationItemEstimate> items)
    {
        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("Collection id cannot be empty.", nameof(collectionId));
        }

        if (requestRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestRevision));
        }

        if (scaledTotal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaledTotal));
        }

        if (scale < 0 || scale > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        Currency = Require(currency, nameof(currency)).ToUpperInvariant();
        PricingProvenanceId = Require(pricingProvenanceId, nameof(pricingProvenanceId));
        ArgumentNullException.ThrowIfNull(items);

        var materialized = items.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A collection estimate requires at least one item.", nameof(items));
        }

        if (materialized.Select(static item => item.ItemId).Distinct().Count() != materialized.Length)
        {
            throw new ArgumentException("Collection estimate item ids must be unique.", nameof(items));
        }

        if (materialized.Any(item => !string.Equals(item.Currency, Currency, StringComparison.Ordinal) || item.Scale != scale))
        {
            throw new ArgumentException("Every item estimate must use the collection currency and exact scale.", nameof(items));
        }

        var computed = materialized.Aggregate(0L, static (sum, item) => checked(sum + item.ScaledAmount));
        if (computed != scaledTotal)
        {
            throw new ArgumentException("Collection total must exactly equal the sum of item estimates.", nameof(scaledTotal));
        }

        CollectionId = collectionId;
        RequestRevision = requestRevision;
        EstimatedAtUtc = estimatedAtUtc.ToUniversalTime();
        ScaledTotal = scaledTotal;
        Scale = scale;
        Items = new ReadOnlyCollection<GenerationItemEstimate>(materialized);
    }

    public Guid CollectionId { get; }

    public int RequestRevision { get; }

    public DateTimeOffset EstimatedAtUtc { get; }

    public string Currency { get; }

    public long ScaledTotal { get; }

    public int Scale { get; }

    public string PricingProvenanceId { get; }

    public IReadOnlyList<GenerationItemEstimate> Items { get; }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record GenerationItemEstimate(Guid ItemId, int Order, string Currency, long ScaledAmount, int Scale)
{
    public Guid ItemId { get; init; } = ItemId != Guid.Empty
        ? ItemId
        : throw new ArgumentException("Item id cannot be empty.", nameof(ItemId));

    public int Order { get; init; } = Order >= 0
        ? Order
        : throw new ArgumentOutOfRangeException(nameof(Order));

    public string Currency { get; init; } = Require(Currency).ToUpperInvariant();

    public long ScaledAmount { get; init; } = ScaledAmount >= 0
        ? ScaledAmount
        : throw new ArgumentOutOfRangeException(nameof(ScaledAmount));

    public int Scale { get; init; } = Scale is >= 0 and <= 12
        ? Scale
        : throw new ArgumentOutOfRangeException(nameof(Scale));

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

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

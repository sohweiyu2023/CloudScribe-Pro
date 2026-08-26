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

namespace CloudScribe.Domain.Pricing;

public sealed record PricingEstimateRequest
{
    public PricingEstimateRequest(
        long quantity,
        string quantityUnitId,
        CostUsageScope usageScope,
        string provenanceId,
        string? regionId = null,
        bool taxResolved = false,
        bool creditsResolved = false,
        bool foreignExchangeResolved = false,
        bool catalogIsStale = false,
        bool catalogIsConflicting = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);

        if (!Enum.IsDefined(usageScope))
        {
            ThrowUndefinedUsageScope(nameof(usageScope));
        }

        Quantity = quantity;
        QuantityUnitId = PricingMeterDefinition.NormalizeStableToken(quantityUnitId, nameof(quantityUnitId));
        UsageScope = usageScope;
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceId);
        ProvenanceId = provenanceId.Trim();
        if (ProvenanceId.Length > 160
            || ProvenanceId.Any(static character =>
                char.IsControl(character)
                || char.IsSurrogate(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
        {
            throw new ArgumentException("Pricing provenance is limited to 160 visible characters.", nameof(provenanceId));
        }

        RegionId = regionId is null
            ? null
            : PricingMeterDefinition.NormalizeStableToken(regionId, nameof(regionId));
        TaxResolved = taxResolved;
        CreditsResolved = creditsResolved;
        ForeignExchangeResolved = foreignExchangeResolved;
        CatalogIsStale = catalogIsStale;
        CatalogIsConflicting = catalogIsConflicting;
    }

    private static void ThrowUndefinedUsageScope(string parameterName) =>
        throw new ArgumentOutOfRangeException(parameterName);

    public long Quantity { get; }
    public string QuantityUnitId { get; }
    public CostUsageScope UsageScope { get; }
    public string ProvenanceId { get; }
    public string? RegionId { get; }
    public bool TaxResolved { get; }
    public bool CreditsResolved { get; }
    public bool ForeignExchangeResolved { get; }
    public bool CatalogIsStale { get; }
    public bool CatalogIsConflicting { get; }
}

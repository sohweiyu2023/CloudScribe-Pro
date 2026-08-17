namespace CloudScribe.Domain.Pricing;

public sealed record PricingModifier
{
    public PricingModifier(string stableId, long numerator, long denominator, string? regionId = null)
    {
        StableId = PricingMeterDefinition.NormalizeStableToken(stableId, nameof(stableId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);

        Numerator = numerator;
        Denominator = denominator;
        RegionId = regionId is null
            ? null
            : PricingMeterDefinition.NormalizeStableToken(regionId, nameof(regionId));
    }

    public string StableId { get; }
    public long Numerator { get; }
    public long Denominator { get; }
    public string? RegionId { get; }
}

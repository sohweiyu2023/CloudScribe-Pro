namespace CloudScribe.Domain.Pricing;

public sealed record PricingAllowance
{
    public PricingAllowance(long includedQuantity, CostUsageScope scope)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(includedQuantity);

        IncludedQuantity = includedQuantity;
        Scope = scope;
    }

    public long IncludedQuantity { get; }
    public CostUsageScope Scope { get; }
}

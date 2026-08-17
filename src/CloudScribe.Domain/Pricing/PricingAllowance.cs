namespace CloudScribe.Domain.Pricing;

public sealed record PricingAllowance
{
    public PricingAllowance(long includedQuantity, CostUsageScope scope)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(includedQuantity);

        if (!Enum.IsDefined(scope))
        {
            ThrowUndefinedScope(nameof(scope));
        }

        IncludedQuantity = includedQuantity;
        Scope = scope;
    }

    private static void ThrowUndefinedScope(string parameterName) =>
        throw new ArgumentOutOfRangeException(parameterName);

    public long IncludedQuantity { get; }
    public CostUsageScope Scope { get; }
}

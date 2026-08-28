using System.Numerics;
using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Pricing;

public sealed class PricingCostEngine
{
    public static CostAssessment Estimate(PricingMeterDefinition meter, PricingEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(request);

        CostAssessment? unavailable = ValidateRequest(meter, request);
        if (unavailable is not null)
        {
            return unavailable;
        }

        long billableQuantity = ApplyAllowance(meter.Allowance, request);
        ExactMoney prototype = meter.Tiers[0].PricePerBlock;
        BigInteger totalUnits = CalculateTieredUnits(meter.Tiers, billableQuantity);
        (BigInteger minimumUnits, BigInteger maximumUnits) = ApplyModifiers(totalUnits, meter.Modifiers, request.RegionId);

        if (minimumUnits > long.MaxValue || maximumUnits > long.MaxValue)
        {
            return CostAssessment.Unknown(
                request.UsageScope,
                "Calculated cost exceeds the exact-money representation limit; no truncated amount is shown.",
                request.CatalogIsStale,
                request.CatalogIsConflicting);
        }

        var minimum = new ExactMoney((long)minimumUnits, prototype.Scale, prototype.CurrencyCode);
        var maximum = new ExactMoney((long)maximumUnits, prototype.Scale, prototype.CurrencyCode);
        return CostAssessment.Estimate(
            minimum,
            maximum,
            request.UsageScope,
            request.ProvenanceId,
            "Normalized pricing meter estimate; tax, credits and FX were explicitly resolved by the caller.",
            request.CatalogIsStale,
            request.CatalogIsConflicting);
    }

    private static CostAssessment? ValidateRequest(PricingMeterDefinition meter, PricingEstimateRequest request)
    {
        if (!string.Equals(meter.QuantityUnitId, request.QuantityUnitId, StringComparison.Ordinal))
        {
            return CostAssessment.Unknown(
                request.UsageScope,
                $"Pricing meter unit '{meter.QuantityUnitId}' does not match request unit '{request.QuantityUnitId}'.");
        }

        if (!request.TaxResolved || !request.CreditsResolved || !request.ForeignExchangeResolved)
        {
            return CostAssessment.Unknown(
                request.UsageScope,
                "Tax, credits, or foreign exchange treatment is unresolved; CloudScribe will not guess a final amount.",
                request.CatalogIsStale,
                request.CatalogIsConflicting);
        }

        return null;
    }

    private static long ApplyAllowance(PricingAllowance? allowance, PricingEstimateRequest request)
    {
        if (allowance is null || allowance.Scope != request.UsageScope)
        {
            return request.Quantity;
        }

        return Math.Max(0, request.Quantity - Math.Min(request.Quantity, allowance.IncludedQuantity));
    }

    private static BigInteger CalculateTieredUnits(IReadOnlyList<PricingTier> tiers, long quantity)
    {
        BigInteger totalUnits = BigInteger.Zero;
        long remaining = quantity;
        long consumed = 0;

        foreach (PricingTier tier in tiers)
        {
            if (remaining == 0)
            {
                break;
            }

            long tierCapacity = tier.ThroughQuantity is null
                ? remaining
                : checked(tier.ThroughQuantity.Value - consumed);
            long tierQuantity = Math.Min(remaining, tierCapacity);
            long blocks = CeilingDivide(tierQuantity, tier.BlockSize);
            totalUnits += (BigInteger)blocks * tier.PricePerBlock.Units;
            remaining -= tierQuantity;
            consumed = checked(consumed + tierQuantity);
        }

        return totalUnits;
    }

    private static (BigInteger Minimum, BigInteger Maximum) ApplyModifiers(
        BigInteger totalUnits,
        IReadOnlyList<PricingModifier> modifiers,
        string? regionId)
    {
        BigInteger minimumUnits = totalUnits;
        BigInteger maximumUnits = totalUnits;
        foreach (PricingModifier modifier in modifiers)
        {
            if (modifier.RegionId is not null
                && !string.Equals(modifier.RegionId, regionId, StringComparison.Ordinal))
            {
                continue;
            }

            minimumUnits = FloorMultiplyDivide(minimumUnits, modifier.Numerator, modifier.Denominator);
            maximumUnits = CeilingMultiplyDivide(maximumUnits, modifier.Numerator, modifier.Denominator);
        }

        return (minimumUnits, maximumUnits);
    }

    private static long CeilingDivide(long numerator, long denominator)
    {
        if (numerator == 0)
        {
            return 0;
        }

        BigInteger quotient = ((BigInteger)numerator + denominator - 1) / denominator;
        if (quotient > long.MaxValue)
        {
            throw new OverflowException("Pricing block count exceeds the supported exact-integer range.");
        }

        return (long)quotient;
    }

    private static BigInteger FloorMultiplyDivide(BigInteger value, long numerator, long denominator) =>
        value * numerator / denominator;

    private static BigInteger CeilingMultiplyDivide(BigInteger value, long numerator, long denominator)
    {
        BigInteger product = value * numerator;
        return product == 0 ? BigInteger.Zero : (product + denominator - 1) / denominator;
    }
}

using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Pricing;

public sealed record PricingMeterDefinition
{
    public PricingMeterDefinition(
        string stableId,
        string quantityUnitId,
        IReadOnlyList<PricingTier> tiers,
        PricingAllowance? allowance = null,
        IReadOnlyList<PricingModifier>? modifiers = null)
    {
        StableId = NormalizeStableToken(stableId, nameof(stableId));
        QuantityUnitId = NormalizeStableToken(quantityUnitId, nameof(quantityUnitId));
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0)
        {
            throw new ArgumentException("A pricing meter requires at least one tier.", nameof(tiers));
        }

        PricingTier[] copiedTiers = [.. tiers];
        ValidateTiers(copiedTiers);
        Tiers = copiedTiers;
        Allowance = allowance;
        Modifiers = modifiers is null ? [] : [.. modifiers];
    }

    public string StableId { get; }
    public string QuantityUnitId { get; }
    public IReadOnlyList<PricingTier> Tiers { get; }
    public PricingAllowance? Allowance { get; }
    public IReadOnlyList<PricingModifier> Modifiers { get; }

    internal static string NormalizeStableToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length is > 80
            || normalized.Any(static character =>
                character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "Stable pricing identifiers must use 1-80 lowercase ASCII letters, digits, hyphen, underscore, or period.",
                parameterName);
        }

        return normalized;
    }

    private static void ValidateTiers(PricingTier[] tiers)
    {
        long previousBoundary = 0;
        string? currency = null;
        int? scale = null;
        for (int index = 0; index < tiers.Length; index++)
        {
            PricingTier tier = tiers[index]
                ?? throw new ArgumentException("Pricing tiers cannot contain null entries.", nameof(tiers));
            tier.PricePerBlock.EnsureValid(nameof(tiers));
            if (tier.PricePerBlock.Units < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tiers), "Pricing tiers cannot contain negative prices.");
            }

            if (currency is null)
            {
                currency = tier.PricePerBlock.CurrencyCode;
                scale = tier.PricePerBlock.Scale;
            }
            else if (!string.Equals(currency, tier.PricePerBlock.CurrencyCode, StringComparison.Ordinal)
                || scale != tier.PricePerBlock.Scale)
            {
                throw new ArgumentException("All tiers in a meter must use one currency and exact integer scale.", nameof(tiers));
            }

            if (tier.ThroughQuantity is null)
            {
                if (index != tiers.Length - 1)
                {
                    throw new ArgumentException("Only the final pricing tier can be open-ended.", nameof(tiers));
                }

                return;
            }

            if (tier.ThroughQuantity <= previousBoundary)
            {
                throw new ArgumentException("Pricing tier boundaries must be strictly increasing.", nameof(tiers));
            }

            previousBoundary = tier.ThroughQuantity.Value;
        }

        throw new ArgumentException("The final pricing tier must be open-ended so estimates never silently extrapolate.", nameof(tiers));
    }
}

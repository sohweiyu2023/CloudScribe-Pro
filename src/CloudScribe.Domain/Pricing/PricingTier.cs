using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Pricing;

public sealed record PricingTier
{
    public PricingTier(long? throughQuantity, long blockSize, ExactMoney pricePerBlock)
    {
        if (throughQuantity is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(throughQuantity));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        pricePerBlock.EnsureValid(nameof(pricePerBlock));
        if (pricePerBlock.Units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pricePerBlock));
        }

        ThroughQuantity = throughQuantity;
        BlockSize = blockSize;
        PricePerBlock = pricePerBlock;
    }

    public long? ThroughQuantity { get; }
    public long BlockSize { get; }
    public ExactMoney PricePerBlock { get; }
}

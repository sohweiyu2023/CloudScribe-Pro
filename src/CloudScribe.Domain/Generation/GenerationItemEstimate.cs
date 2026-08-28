namespace CloudScribe.Domain.Generation;

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

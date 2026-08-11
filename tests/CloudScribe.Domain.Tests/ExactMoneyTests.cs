using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Tests;

public sealed class ExactMoneyTests
{
    [Fact]
    public void PreservesScaledIntegerValue()
    {
        ExactMoney money = new(12345, 2, "usd");

        Assert.Equal("USD", money.CurrencyCode);
        Assert.Equal(123.45m, money.ToDecimal());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void RejectsInvalidScale(int scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExactMoney(1, scale, "USD"));
    }
}

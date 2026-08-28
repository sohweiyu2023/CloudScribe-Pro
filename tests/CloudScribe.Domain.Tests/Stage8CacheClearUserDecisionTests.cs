using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8CacheClearUserDecisionTests
{
    [Fact]
    public void ClearRequiresExplicitConfirmationAndNeverClaimsSecureErase()
    {
        var denied = CacheClearUserDecisionPolicy.Create(userConfirmedClear: false);
        Assert.False(denied.MayClearUnprotectedEntries);
        Assert.False(denied.ClaimsSecureErase);
        Assert.Contains("provider", denied.Warning, StringComparison.OrdinalIgnoreCase);

        var allowed = CacheClearUserDecisionPolicy.Create(userConfirmedClear: true);
        Assert.True(allowed.MayClearUnprotectedEntries);
        Assert.False(allowed.ClaimsSecureErase);
        Assert.Equal(denied.Warning, allowed.Warning);
    }

    [Fact]
    public void CostAvoidanceIsOnlyDisplayedAsAnExplicitEstimate()
    {
        var decision = CacheClearUserDecisionPolicy.Create(
            userConfirmedClear: true,
            currencyCode: "USD",
            estimatedAvoidedScaledUnits: 1234,
            scale: 2);

        Assert.NotNull(decision.EstimatedCostAvoidance);
        Assert.Contains("estimated", decision.EstimatedCostAvoidance!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USD", decision.EstimatedCostAvoidance!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateWithoutCurrencyFailsClosed()
    {
        Assert.Throws<ArgumentException>(() => CacheClearUserDecisionPolicy.Create(
            userConfirmedClear: true,
            estimatedAvoidedScaledUnits: 100));
    }
}

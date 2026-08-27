using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8CacheClearUserDecisionTests
{
    [Fact]
    public void Clear_requires_explicit_confirmation_and_never_claims_secure_erase()
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
    public void Cost_avoidance_is_only_displayed_as_an_explicit_estimate()
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
    public void Estimate_without_currency_fails_closed()
    {
        Assert.Throws<ArgumentException>(() => CacheClearUserDecisionPolicy.Create(
            userConfirmedClear: true,
            estimatedAvoidedScaledUnits: 100));
    }
}

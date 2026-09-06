using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7ProviderRoutingPolicyTests
{
    [Fact]
    public void ExactPinnedRouteWinsWithoutFallback()
    {
        var requested = Route("p1", "a1", "op", "v1", "price-1", 100, "USD");
        var other = Route("p2", "a2", "op", "v2", "price-1", 50, "USD");

        var decision = new ProviderRoutingPolicy().Select(requested, [other, requested], false, 100, "USD");

        Assert.False(decision.UsedFallback);
        Assert.Equal(requested, decision.Selected);
    }

    [Fact]
    public void MissingPinnedRouteFailsWhenFallbackNotAuthorized()
    {
        var requested = Route("p1", "a1", "op", "v1", "price-1", 100, "USD");

        Assert.Throws<InvalidOperationException>(() =>
            new ProviderRoutingPolicy().Select(requested, [Route("p2", "a2", "op", "v2", "price-1", 50, "USD")], false, 100, "USD"));
    }

    [Fact]
    public void FallbackCannotSilentlyChangePricingProvenance()
    {
        var requested = Route("p1", "a1", "op", "v1", "price-1", 100, "USD");
        var candidate = Route("p2", "a2", "op", "v2", "price-2", 50, "USD");

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ProviderRoutingPolicy().Select(requested, [candidate], true, 100, "USD"));

        Assert.Contains("pricing provenance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallbackRejectsCurrencyOrSpendViolation()
    {
        var requested = Route("p1", "a1", "op", "v1", "price-1", 100, "USD");
        var tooExpensive = Route("p2", "a2", "op", "v2", "price-1", 101, "USD");
        var wrongCurrency = Route("p3", "a3", "op", "v3", "price-1", 10, "EUR");

        Assert.Throws<InvalidOperationException>(() =>
            new ProviderRoutingPolicy().Select(requested, [tooExpensive, wrongCurrency], true, 100, "USD"));
    }

    private static ProviderRoute Route(string p, string a, string op, string v, string provenance, long cost, string currency) =>
        new(p, a, op, v, provenance, cost, currency);
}

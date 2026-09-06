using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7ProviderRouteOutagePolicyTests
{
    [Fact]
    public void AvailablePinnedRouteAlwaysWins()
    {
        var pinned = Route("provider-a", "account-a", "voice-a", 'a', 'b');
        var selected = ProviderRouteOutagePolicy.Resolve(
            pinned,
            pinnedRouteAvailable: true,
            proposedFallback: null,
            fallbackAuthorization: null,
            billedCurrency: "USD",
            billedCurrencyScale: 6,
            projectedFallbackMinorUnits: 0);

        Assert.Equal(pinned, selected);
    }

    [Fact]
    public void OutageWithoutExplicitAuthorizationFailsClosed()
    {
        var pinned = Route("provider-a", "account-a", "voice-a", 'a', 'b');
        var fallback = Route("provider-b", "account-b", "voice-b", 'c', 'd');

        Assert.Throws<InvalidOperationException>(() => ProviderRouteOutagePolicy.Resolve(
            pinned, false, fallback, null, "USD", 6, 100));
    }

    [Fact]
    public void ExplicitFallbackCannotChangeRouteOrExceedSpendCeiling()
    {
        var pinned = Route("provider-a", "account-a", "voice-a", 'a', 'b');
        var fallback = Route("provider-b", "account-b", "voice-b", 'c', 'd');
        var other = Route("provider-c", "account-c", "voice-c", 'e', 'f');
        var authorization = new ProviderRouteFallbackAuthorization(pinned, fallback, "USD", 6, 500);

        Assert.Equal(fallback, ProviderRouteOutagePolicy.Resolve(
            pinned, false, fallback, authorization, "USD", 6, 500));

        Assert.Throws<InvalidOperationException>(() => ProviderRouteOutagePolicy.Resolve(
            pinned, false, other, authorization, "USD", 6, 100));
        Assert.Throws<InvalidOperationException>(() => ProviderRouteOutagePolicy.Resolve(
            pinned, false, fallback, authorization, "USD", 6, 501));
        Assert.Throws<InvalidOperationException>(() => ProviderRouteOutagePolicy.Resolve(
            pinned, false, fallback, authorization, "EUR", 6, 100));
    }

    private static ProviderRouteIdentity Route(string provider, string account, string voice, char pricing, char capability) =>
        new(provider, account, "synthesize", voice, new string(pricing, 64), new string(capability, 64));
}

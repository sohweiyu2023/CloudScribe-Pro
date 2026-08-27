using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7MultiSpeakerTurnSpendAuthorizationTests
{
    [Fact]
    public void ExactPinnedRouteAndSpendAreAuthorized()
    {
        var binding = Binding("narrator", "voice-a", "pricing:p1");
        var authorization = new MultiSpeakerTurnSpendAuthorization(
            "narrator", binding.RouteIdentity, binding.PricingProvenanceId, "USD", 4, 2500).Validate();

        authorization.EnsureAuthorized(binding, "USD", 4, 2499);
    }

    [Fact]
    public void RoutePricingCurrencyAndCeilingDriftFailClosed()
    {
        var binding = Binding("narrator", "voice-a", "pricing:p1");
        var authorization = new MultiSpeakerTurnSpendAuthorization(
            "narrator", binding.RouteIdentity, binding.PricingProvenanceId, "USD", 4, 2500).Validate();

        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized(Binding("narrator", "voice-b", "pricing:p1"), "USD", 4, 100));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized(Binding("narrator", "voice-a", "pricing:p2"), "USD", 4, 100));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized(binding, "EUR", 4, 100));
        Assert.Throws<InvalidOperationException>(() => authorization.EnsureAuthorized(binding, "USD", 4, 2501));
    }

    [Fact]
    public void AuthorizationSetMustExactlyCoverVoiceMap()
    {
        var narrator = Binding("narrator", "voice-a", "pricing:p1");
        var guest = Binding("guest", "voice-b", "pricing:p1");
        var map = new MultiSpeakerVoiceMap([narrator, guest]);
        var set = new MultiSpeakerTurnSpendAuthorizationSet([
            new("narrator", narrator.RouteIdentity, narrator.PricingProvenanceId, "USD", 4, 2500),
        ]);

        Assert.Throws<InvalidOperationException>(() => set.Validate(map));
    }

    private static SpeakerVoiceBinding Binding(string role, string voice, string pricing) => new(
        role,
        "provider:release1",
        "account:primary",
        voice,
        pricing,
        "capabilities:1");
}

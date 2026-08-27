using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceAuditionSpendPolicyTests
{
    [Fact]
    public void Cache_hit_avoids_spend_but_force_fresh_requires_approval()
    {
        var hit = VoiceAuditionSpendPolicy.Evaluate(
            cacheHitEligible: true,
            forceFresh: false,
            explicitSpendApproved: false,
            capabilityCurrent: true,
            pricingCurrent: true);
        Assert.True(hit.MayReuseCache);
        Assert.False(hit.MaySubmitBillableRequest);

        var fresh = VoiceAuditionSpendPolicy.Evaluate(
            cacheHitEligible: true,
            forceFresh: true,
            explicitSpendApproved: true,
            capabilityCurrent: true,
            pricingCurrent: true);
        Assert.False(fresh.MayReuseCache);
        Assert.True(fresh.MaySubmitBillableRequest);
    }

    [Fact]
    public void Stale_capability_or_pricing_fails_closed_without_throwing()
    {
        var staleCapability = VoiceAuditionSpendPolicy.Evaluate(false, false, true, false, true);
        Assert.False(staleCapability.MayReuseCache);
        Assert.False(staleCapability.MaySubmitBillableRequest);

        var stalePricing = VoiceAuditionSpendPolicy.Evaluate(false, false, true, true, false);
        Assert.False(stalePricing.MayReuseCache);
        Assert.False(stalePricing.MaySubmitBillableRequest);
    }
}

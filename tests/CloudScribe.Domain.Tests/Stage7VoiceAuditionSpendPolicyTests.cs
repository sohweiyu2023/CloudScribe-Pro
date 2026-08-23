using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceAuditionSpendPolicyTests
{
    [Fact]
    public void Cache_hit_avoids_spend_but_force_fresh_requires_approval()
    {
        var hit = VoiceAuditionSpendPolicy.Decide(cacheEligible: true, forceFresh: false, capabilityCurrent: true, pricingCurrent: true, spendApproved: false);
        Assert.True(hit.UseCache);
        Assert.False(hit.SubmitBillableRequest);

        var fresh = VoiceAuditionSpendPolicy.Decide(cacheEligible: true, forceFresh: true, capabilityCurrent: true, pricingCurrent: true, spendApproved: true);
        Assert.False(fresh.UseCache);
        Assert.True(fresh.SubmitBillableRequest);
    }

    [Fact]
    public void Stale_capability_or_pricing_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => VoiceAuditionSpendPolicy.Decide(false, false, false, true, true));
        Assert.Throws<InvalidOperationException>(() => VoiceAuditionSpendPolicy.Decide(false, false, true, false, true));
    }
}

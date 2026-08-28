using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceAuditionExecutionGateTests
{
    [Fact]
    public void EligibleCacheHitAuthorizesReuseWithoutProviderSpend()
    {
        var authorization = VoiceAuditionExecutionGate.Authorize(
            cacheHitEligible: true,
            forceFresh: false,
            explicitSpendApproved: false,
            capabilityCurrent: true,
            pricingCurrent: true);

        Assert.True(authorization.UseCachedMedia);
        Assert.False(authorization.SubmitProviderRequest);
        Assert.Equal("voice-audition-cache-hit", authorization.Reason);
    }

    [Fact]
    public void ForceFreshRequiresExplicitSpendApproval()
    {
        Assert.Throws<InvalidOperationException>(() => VoiceAuditionExecutionGate.Authorize(
            cacheHitEligible: true,
            forceFresh: true,
            explicitSpendApproved: false,
            capabilityCurrent: true,
            pricingCurrent: true));

        var authorized = VoiceAuditionExecutionGate.Authorize(
            cacheHitEligible: true,
            forceFresh: true,
            explicitSpendApproved: true,
            capabilityCurrent: true,
            pricingCurrent: true);

        Assert.False(authorized.UseCachedMedia);
        Assert.True(authorized.SubmitProviderRequest);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StaleCapabilityOrPricingFailsClosed(bool capabilityCurrent, bool pricingCurrent)
    {
        Assert.Throws<InvalidOperationException>(() => VoiceAuditionExecutionGate.Authorize(
            cacheHitEligible: false,
            forceFresh: false,
            explicitSpendApproved: true,
            capabilityCurrent: capabilityCurrent,
            pricingCurrent: pricingCurrent));
    }
}

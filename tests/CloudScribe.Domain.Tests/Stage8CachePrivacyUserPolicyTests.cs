using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8CachePrivacyUserPolicyTests
{
    [Fact]
    public void ProjectBundlesExcludeCacheUnlessUserAndPolicyBothExplicitlyAllowIt()
    {
        Assert.False(CachePrivacyUserPolicy.IncludeCacheInProjectExportsByDefault);
        Assert.False(CachePrivacyUserPolicy.MayIncludeCacheInProjectBundle(false, false));
        Assert.False(CachePrivacyUserPolicy.MayIncludeCacheInProjectBundle(true, false));
        Assert.False(CachePrivacyUserPolicy.MayIncludeCacheInProjectBundle(false, true));
        Assert.True(CachePrivacyUserPolicy.MayIncludeCacheInProjectBundle(true, true));
    }

    [Fact]
    public void NormalClearWarnsAboutFutureProviderCostWithoutClaimingSecureErase()
    {
        Assert.Contains("may incur new provider charges", CachePrivacyUserPolicy.ClearCacheWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unresolved-submission", CachePrivacyUserPolicy.ClearCacheWarning, StringComparison.OrdinalIgnoreCase);
        Assert.False(CachePrivacyUserPolicy.ClaimSecureEraseOnNormalClear);
    }

    [Fact]
    public void CostAvoidanceLanguageIsExplicitlyEstimatedNotGuaranteedSavings()
    {
        var message = CachePrivacyUserPolicy.DescribeEstimatedCostAvoidance("USD", 1234, 2);

        Assert.Contains("USD 12.34", message, StringComparison.Ordinal);
        Assert.Contains("Estimated provider-billed cost avoided", message, StringComparison.Ordinal);
        Assert.Contains("not a guarantee of money saved", message, StringComparison.OrdinalIgnoreCase);
    }
}

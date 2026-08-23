using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5CacheProtectionPolicyTests
{
    [Fact]
    public void Combined_lifecycle_protection_is_not_evictable()
    {
        var protection = GenerationCacheProtectionPolicy.Combine(
            active: true,
            pinned: false,
            referenced: true,
            unresolvedSubmission: true);

        Assert.True(protection.HasFlag(GenerationCacheEntryProtection.Active));
        Assert.True(protection.HasFlag(GenerationCacheEntryProtection.Referenced));
        Assert.True(protection.HasFlag(GenerationCacheEntryProtection.UnresolvedSubmission));
        Assert.False(GenerationCacheProtectionPolicy.MayEvict(protection));
    }

    [Fact]
    public void Completed_entries_become_evictable_only_when_no_other_protection_remains()
    {
        var completed = GenerationCacheProtectionPolicy.ForState(GenerationCacheLifecycleState.Completed);
        Assert.Equal(GenerationCacheEntryProtection.None, completed);
        Assert.True(GenerationCacheProtectionPolicy.MayEvict(completed));
    }
}

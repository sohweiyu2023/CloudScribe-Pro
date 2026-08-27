using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleCacheReuseEligibilityTests
{
    [Fact]
    public void Exact_context_is_reusable_and_any_identity_drift_is_rejected()
    {
        var current = Context("capability-v1", "voice-fingerprint-v1");
        Assert.True(GoogleCacheReuseEligibility.IsEligible(current, current with { }));
        Assert.False(GoogleCacheReuseEligibility.IsEligible(current, Context("capability-v2", "voice-fingerprint-v1")));
        Assert.False(GoogleCacheReuseEligibility.IsEligible(current, Context("capability-v1", "voice-fingerprint-v2")));
    }

    private static GenerationCacheTrustContext Context(string capability, string fingerprint) => new(
        "google-cloud-tts", "account-a", "project-a", "endpoint-a", "region-a", "synthesize",
        "model-a", "voice-a", fingerprint, "speech-plan-a", "en-US", "controls-a", "wav",
        "pcm-24000-mono", "adapter-v1", "compiler-v1", "ast-v1", "normalization-v1",
        "pricing-v1", capability, "governance-v1", "features-v1", "account-capability-v1");
}

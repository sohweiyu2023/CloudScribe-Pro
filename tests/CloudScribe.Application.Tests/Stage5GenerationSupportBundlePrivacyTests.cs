using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5GenerationSupportBundlePrivacyTests
{
    [Fact]
    public void Metadata_only_bundle_never_exposes_generation_payload_or_cache_material()
    {
        var service = new GenerationSupportBundleService();
        var bundle = service.CreateMetadataOnly(
            userExplicitlyRequestedDiagnosticBundle: true,
            currentPolicyAllowsDiagnostics: true,
            new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow));

        Assert.Equal("support-bundle-metadata-only", bundle.PrivacyDecision.Reason);
        Assert.False(bundle.PrivacyDecision.IncludeCacheMedia);
        Assert.False(bundle.PrivacyDecision.IncludeCompiledPayload);
        Assert.False(bundle.PrivacyDecision.IncludeSourceText);
        Assert.False(bundle.PrivacyDecision.IncludePrivateCacheLookupKey);
    }

    [Fact]
    public void Bundle_creation_fails_closed_without_explicit_request_or_policy_authorization()
    {
        var service = new GenerationSupportBundleService();
        var metadata = new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-001", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => service.CreateMetadataOnly(false, true, metadata));
        Assert.Throws<InvalidOperationException>(() => service.CreateMetadataOnly(true, false, metadata));
    }
}

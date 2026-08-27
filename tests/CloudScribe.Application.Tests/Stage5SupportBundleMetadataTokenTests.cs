using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5SupportBundleMetadataTokenTests
{
    [Fact]
    public void MetadataOnlyBundleAcceptsBoundedTokens()
    {
        var service = new GenerationSupportBundleService();
        var bundle = service.CreateMetadataOnly(
            userExplicitlyRequestedDiagnosticBundle: true,
            currentPolicyAllowsDiagnostics: true,
            new GenerationSupportBundleMetadata("1.0.0", "windows-x64", "GEN-CACHE-001", DateTimeOffset.UtcNow));

        Assert.Equal("GEN-CACHE-001", bundle.Metadata.DiagnosticCode);
        Assert.False(bundle.PrivacyDecision.IncludeCacheMedia);
        Assert.False(bundle.PrivacyDecision.IncludeCompiledPayload);
        Assert.False(bundle.PrivacyDecision.IncludeSourceText);
        Assert.False(bundle.PrivacyDecision.IncludePrivateCacheLookupKey);
    }

    [Theory]
    [InlineData("source text goes here")]
    [InlineData("GEN/CACHE/001")]
    [InlineData("GEN\nCACHE")]
    public void DiagnosticCodeRejectsFreeFormOrPathLikeContent(string code)
    {
        var service = new GenerationSupportBundleService();
        Assert.Throws<InvalidOperationException>(() => service.CreateMetadataOnly(
            true,
            true,
            new GenerationSupportBundleMetadata("1.0.0", "windows-x64", code, DateTimeOffset.UtcNow)));
    }
}

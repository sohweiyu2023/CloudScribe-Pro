using System.Security.Cryptography;
using System.Text;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5PrivateCacheNamespaceTests
{
    [Fact]
    public void HmacSha256MatchesRfc4231TestCase1()
    {
        var key = Enumerable.Repeat((byte)0x0b, 20).ToArray();
        var data = Encoding.ASCII.GetBytes("Hi There");

        var actual = PrivateCacheLookupKey.ComputeHmacSha256Hex(key, data);

        Assert.Equal("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7", actual);
    }

    [Fact]
    public void LookupIdentifierSeparatesAccountProjectRegionModelVoiceAndPolicy()
    {
        var key = Enumerable.Range(0, 32).Select(static index => (byte)index).ToArray();
        var payload = Encoding.UTF8.GetBytes("private text that must never become a public raw digest");
        var baseline = CreateContext();
        var baselineKey = PrivateCacheLookupKey.Derive(key, baseline, payload);

        var variants = new[]
        {
            baseline with { AccountId = "account-b" },
            baseline with { ProjectId = "project-b" },
            baseline with { RegionId = "eu-west" },
            baseline with { ResolvedModelId = "model-snapshot-b" },
            baseline with { VoiceStableId = "voice-b" },
            baseline with { GovernancePolicyIdentity = "policy-b" },
            baseline with { CapabilityIdentity = "capabilities-b" },
        };

        Assert.All(variants, variant =>
            Assert.NotEqual(baselineKey.HmacSha256, PrivateCacheLookupKey.Derive(key, variant, payload).HmacSha256));
    }

    [Fact]
    public void PrivateLookupIdentifierIsNotRawPayloadSha256()
    {
        var key = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        var payload = Encoding.UTF8.GetBytes("sensitive transformed synthesis text");
        var lookup = PrivateCacheLookupKey.Derive(key, CreateContext(), payload);
        var rawPayloadSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        Assert.NotEqual(rawPayloadSha, lookup.HmacSha256);
        Assert.Equal(64, lookup.HmacSha256.Length);
        Assert.DoesNotContain("sensitive", lookup.HmacSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static GenerationCacheTrustContext CreateContext() => new(
        ProviderStableId: "provider-a",
        AccountId: "account-a",
        ProjectId: "project-a",
        EndpointId: "endpoint-a",
        RegionId: "us-central1",
        OperationStableId: "synthesize-speech",
        ResolvedModelId: "model-snapshot-a",
        VoiceStableId: "voice-a",
        VoiceFingerprint: "stock-voice-fingerprint-a",
        SpeechPlanIdentity: "speech-plan-schema-v1",
        LanguageTag: "en-SG",
        SynthesisControlsIdentity: "rate=1;pitch=0;volume=0",
        OutputFormat: "wav",
        SampleFormatIdentity: "pcm16-16khz-mono",
        AdapterVersion: "adapter-v1",
        CompilerVersion: "compiler-v1",
        AstVersion: "ast-v1",
        NormalizationVersion: "normalize-v1",
        PricingIdentity: "pricing-v2.23",
        CapabilityIdentity: "capabilities-a",
        GovernancePolicyIdentity: "policy-a",
        ProviderFeatureIdentity: "features-a",
        AccountCapabilityIdentity: "account-capabilities-a");
}

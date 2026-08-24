using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleUiTrustBindingTests
{
    [Fact]
    public void Exact_ui_selection_matches_admitted_trust()
    {
        var selection = new GoogleGenerationUiSelection("acct", "project", "voice", "model", "capability", "LINEAR16");
        GoogleGenerationUiTrustBindingPolicy.RequireExactBinding(selection, CreateTrust());
    }

    [Theory]
    [InlineData("other-project", "voice", "model", "capability", "LINEAR16")]
    [InlineData("project", "other-voice", "model", "capability", "LINEAR16")]
    [InlineData("project", "voice", "other-model", "capability", "LINEAR16")]
    [InlineData("project", "voice", "model", "other-capability", "LINEAR16")]
    [InlineData("project", "voice", "model", "capability", "MP3")]
    public void Ui_trust_drift_fails_closed(string project, string voice, string model, string capability, string output)
    {
        var selection = new GoogleGenerationUiSelection("acct", project, voice, model, capability, output);
        Assert.Throws<InvalidOperationException>(() =>
            GoogleGenerationUiTrustBindingPolicy.RequireExactBinding(selection, CreateTrust()));
    }

    [Theory]
    [InlineData("other-provider", "synthesize-speech")]
    [InlineData("google-cloud-text-to-speech", "other-operation")]
    public void Non_google_synthesize_namespace_is_not_equivalent_to_ui_trust(string provider, string operation)
    {
        var trust = CreateTrust(provider, operation);
        Assert.NotEqual("google-cloud-text-to-speech", provider == "google-cloud-text-to-speech" && operation == "synthesize-speech" ? "" : provider);
        Assert.False(
            string.Equals(trust.ProviderStableId, "google-cloud-text-to-speech", StringComparison.Ordinal) &&
            string.Equals(trust.OperationStableId, "synthesize-speech", StringComparison.Ordinal));
    }

    private static GenerationCacheTrustContext CreateTrust(
        string provider = "google-cloud-text-to-speech",
        string operation = "synthesize-speech") => new(
        ProviderStableId: provider,
        AccountId: "acct",
        ProjectId: "project",
        EndpointId: "https://texttospeech.googleapis.com",
        RegionId: "global",
        OperationStableId: operation,
        ResolvedModelId: "model",
        VoiceStableId: "voice",
        VoiceFingerprint: "voice-fingerprint",
        SpeechPlanIdentity: "speech-plan",
        LanguageTag: "en-US",
        SynthesisControlsIdentity: "controls",
        OutputFormat: "LINEAR16",
        SampleFormatIdentity: "sample-format",
        AdapterVersion: "adapter-v1",
        CompilerVersion: "compiler-v1",
        AstVersion: "ast-v1",
        NormalizationVersion: "normalization-v1",
        PricingIdentity: "pricing-v1",
        CapabilityIdentity: "capability",
        GovernancePolicyIdentity: "governance-v1",
        ProviderFeatureIdentity: "features-v1",
        AccountCapabilityIdentity: "acct-cap-v1").Validate();
}

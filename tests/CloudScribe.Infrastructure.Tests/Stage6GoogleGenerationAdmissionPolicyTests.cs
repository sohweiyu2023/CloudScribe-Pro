using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationAdmissionPolicyTests
{
    [Fact]
    public void AdmissionBindsCurrentAccountVoiceOutputAndCapability()
    {
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var account = new GoogleGenerationAccount(
            "account-a",
            "credential-ref-a",
            new Uri("https://texttospeech.googleapis.com"),
            "global");
        var capability = new GoogleCapabilitySnapshot(
            "account-a",
            "capability/current",
            now.AddMinutes(-5),
            now.AddHours(1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Studio-O" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
            4096);
        var options = new GoogleSpeechCompilationOptions("en-US", "en-US-Studio-O", "LINEAR16", 4096);

        var trust = GoogleGenerationAdmissionPolicy.Admit(
            account, capability, options,
            "project-a", "model-a", "voice-fingerprint-a", "speech-plan/revision-1",
            "controls/default", "pcm-s16le-24khz-mono", "google-adapter/v1",
            "google-compiler/v1", "speech-ast/v1", "normalizer/v1", "pricing/v2.23",
            "governance/default", "google/features/current", "google/account-capabilities/current", now);

        Assert.Equal("account-a", trust.AccountId);
        Assert.Equal("en-US-Studio-O", trust.VoiceStableId);
        Assert.Equal("LINEAR16", trust.OutputFormat);
        Assert.Equal("capability/current", trust.CapabilityIdentity);
    }

    [Fact]
    public void StaleCapabilityCannotPassGenerationAdmission()
    {
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var account = new GoogleGenerationAccount("account-a", "credential-ref-a", new Uri("https://texttospeech.googleapis.com"), "global");
        var capability = new GoogleCapabilitySnapshot(
            "account-a", "capability/stale", now.AddHours(-2), now.AddMinutes(-1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Studio-O" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" }, 4096);
        var options = new GoogleSpeechCompilationOptions("en-US", "en-US-Studio-O", "LINEAR16", 4096);

        Assert.Throws<InvalidOperationException>(() => GoogleGenerationAdmissionPolicy.Admit(
            account, capability, options,
            "project-a", "model-a", "voice-fingerprint-a", "speech-plan/revision-1",
            "controls/default", "pcm-s16le-24khz-mono", "google-adapter/v1",
            "google-compiler/v1", "speech-ast/v1", "normalizer/v1", "pricing/v2.23",
            "governance/default", "google/features/current", "google/account-capabilities/current", now));
    }
}

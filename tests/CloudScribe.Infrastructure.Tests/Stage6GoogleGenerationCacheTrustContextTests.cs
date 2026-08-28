using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationCacheTrustContextTests
{
    [Fact]
    public void AccountProjectRegionModelVoiceAndCapabilityChangesSeparatePrivateCacheNamespace()
    {
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var account = new GoogleGenerationAccount(
            "account-a",
            "credential-ref-a",
            new Uri("https://texttospeech.googleapis.com"),
            "global");
        var capability = new GoogleCapabilitySnapshot(
            "account-a",
            "capability/provenance-a",
            now.AddMinutes(-5),
            now.AddHours(1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Studio-O" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
            4096);
        var options = new GoogleSpeechCompilationOptions("en-US", "en-US-Studio-O", "LINEAR16", 4096);

        var first = Create(account, capability, options, "project-a", "model-a", "fingerprint-a", now);
        var second = Create(account with { AccountId = "account-b", CredentialReferenceId = "credential-ref-b" },
            capability with { AccountId = "account-b", ProvenanceId = "capability/provenance-b" },
            options, "project-b", "model-b", "fingerprint-b", now);

        var key = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var payload = "compiled google request"u8.ToArray();
        var firstLookup = PrivateCacheLookupKey.Derive(key, first, payload);
        var secondLookup = PrivateCacheLookupKey.Derive(key, second, payload);

        Assert.NotEqual(firstLookup.HmacSha256, secondLookup.HmacSha256);
        Assert.Equal("project-a", first.ProjectId);
        Assert.Equal("capability/provenance-a", first.CapabilityIdentity);
        Assert.Equal("en-US-Studio-O", first.VoiceStableId);
    }

    [Fact]
    public void StaleCapabilityEvidenceCannotAuthorizeCacheReuse()
    {
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var account = new GoogleGenerationAccount("account-a", "credential-ref-a", new Uri("https://texttospeech.googleapis.com"), "global");
        var capability = new GoogleCapabilitySnapshot(
            "account-a",
            "capability/stale",
            now.AddHours(-2),
            now.AddMinutes(-1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Studio-O" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
            4096);
        var options = new GoogleSpeechCompilationOptions("en-US", "en-US-Studio-O", "LINEAR16", 4096);

        Assert.Throws<InvalidOperationException>(() => Create(account, capability, options, "project-a", "model-a", "fingerprint-a", now));
    }

    private static GenerationCacheTrustContext Create(
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capability,
        GoogleSpeechCompilationOptions options,
        string project,
        string model,
        string fingerprint,
        DateTimeOffset now) =>
        GoogleGenerationCacheTrustContextFactory.Create(
            account,
            capability,
            options,
            project,
            model,
            fingerprint,
            "speech-plan/revision-1",
            "controls/default",
            "pcm-s16le-24khz-mono",
            "google-adapter/v1",
            "google-compiler/v1",
            "speech-ast/v1",
            "normalizer/v1",
            "pricing/v2.23",
            "governance/default",
            "google/features/current",
            "google/account-capabilities/current",
            now);
}

using System.Globalization;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationSubmissionEnvelopeTests
{
    [Fact]
    public void ExactApprovedIdentityRemainsAuthorized()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture);
        var account = Account();
        var capabilities = Capabilities(now);
        var payload = new byte[] { 1, 2, 3, 4 };
        var envelope = GoogleGenerationSubmissionEnvelope.Create(account, capabilities, "pricing-v1", 7, "voice-a", "LINEAR16", payload, now);

        envelope.EnsureStillAuthorized(account, capabilities, "pricing-v1", 7, payload, now);

        Assert.Equal("account-1", envelope.AccountId);
        Assert.Equal("credential-ref-1", envelope.CredentialReferenceId);
        Assert.Equal(64, envelope.CompiledPayloadSha256.Length);
    }

    [Fact]
    public void PricingOrPayloadDriftInvalidatesBillableSubmission()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture);
        var envelope = GoogleGenerationSubmissionEnvelope.Create(Account(), Capabilities(now), "pricing-v1", 7, "voice-a", "LINEAR16", new byte[] { 1, 2, 3 }, now);

        Assert.Throws<InvalidOperationException>(() => envelope.EnsureStillAuthorized(Account(), Capabilities(now), "pricing-v2", 7, new byte[] { 1, 2, 3 }, now));
        Assert.Throws<InvalidOperationException>(() => envelope.EnsureStillAuthorized(Account(), Capabilities(now), "pricing-v1", 7, new byte[] { 9, 2, 3 }, now));
    }

    [Fact]
    public void StaleCapabilitiesBlockEnvelopeCreation()
    {
        var observed = DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture);
        var staleNow = observed.AddHours(2);

        Assert.Throws<InvalidOperationException>(() => GoogleGenerationSubmissionEnvelope.Create(
            Account(), Capabilities(observed), "pricing-v1", 7, "voice-a", "LINEAR16", new byte[] { 1 }, staleNow));
    }

    private static GoogleGenerationAccount Account() =>
        new("account-1", "credential-ref-1", new Uri("https://texttospeech.googleapis.com/"), "global");

    private static GoogleCapabilitySnapshot Capabilities(DateTimeOffset observed) =>
        new(
            "account-1",
            "capability-v1",
            observed,
            observed.AddHours(1),
            new HashSet<string>(StringComparer.Ordinal) { "voice-a" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
            4096);
}

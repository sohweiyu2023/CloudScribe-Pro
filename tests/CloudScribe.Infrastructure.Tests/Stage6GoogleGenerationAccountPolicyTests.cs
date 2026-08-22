using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationAccountPolicyTests
{
    [Fact]
    public void Account_RejectsEmbeddedCredentialsAndNonHttpsEndpoint()
    {
        Assert.Throws<ArgumentException>(() => new GoogleGenerationAccount(
            "acct", "vault-ref", new Uri("http://texttospeech.googleapis.com"), "global").Validate());
        Assert.Throws<ArgumentException>(() => new GoogleGenerationAccount(
            "acct", "vault-ref", new Uri("https://user:secret@texttospeech.googleapis.com"), "global").Validate());
    }

    [Fact]
    public void CapabilitySnapshot_RejectsStaleOrUnsupportedSubmission()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var snapshot = new GoogleCapabilitySnapshot(
            "acct",
            "capability-source-v1",
            now.AddMinutes(-10),
            now.AddMinutes(-1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Standard-A" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
            4096);

        Assert.Throws<InvalidOperationException>(() => snapshot.RequireSupported(
            "en-US-Standard-A", "LINEAR16", 1024, now));
    }

    [Fact]
    public void CapabilitySnapshot_RequiresVoiceEncodingAndPostCompileLimit()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var snapshot = new GoogleCapabilitySnapshot(
            "acct",
            "capability-source-v1",
            now.AddMinutes(-1),
            now.AddHours(1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Standard-A" },
            new HashSet<string>(StringComparer.Ordinal) { "LINEAR16" },
            4096);

        snapshot.RequireSupported("en-US-Standard-A", "LINEAR16", 4096, now);
        Assert.Throws<InvalidOperationException>(() => snapshot.RequireSupported("missing", "LINEAR16", 100, now));
        Assert.Throws<InvalidOperationException>(() => snapshot.RequireSupported("en-US-Standard-A", "MP3", 100, now));
        Assert.Throws<InvalidOperationException>(() => snapshot.RequireSupported("en-US-Standard-A", "LINEAR16", 4097, now));
    }

    [Fact]
    public void AmbiguousSubmission_AlwaysReconcilesBeforeRetry()
    {
        var disposition = GoogleProviderResponsePolicy.Classify(503, TimeSpan.FromSeconds(5), true);

        Assert.Equal(GoogleRetryDisposition.ReconcileBeforeRetry, disposition.Disposition);
        Assert.Null(disposition.RetryAfter);
    }

    [Fact]
    public void RateLimit_HonorsBoundedRetryAfter()
    {
        var disposition = GoogleProviderResponsePolicy.Classify(429, TimeSpan.FromSeconds(30), false);

        Assert.Equal(GoogleRetryDisposition.RetryAfter, disposition.Disposition);
        Assert.Equal(TimeSpan.FromSeconds(30), disposition.RetryAfter);
    }
}

using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleGenerationBoundQueueCoordinatorTests
{
    [Fact]
    public async Task MismatchedAdmittedAccountNeverReachesSubmit()
    {
        var submitCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator((request, decision, cancellationToken) =>
        {
            submitCalls++;
            return Task.FromResult(new GenerationProviderResponse(
                SubmissionDisposition.Accepted,
                "provider-request",
                new byte[] { 1, 2, 3 },
                "audio/wav",
                null,
                "google-accepted"));
        });
        var coordinator = new GoogleGenerationBoundQueueCoordinator(queue);
        var request = new GenerationProviderRequest(
            "google-cloud-tts", "synthesize", "account-a", "idem", new byte[] { 1 }, "wav");
        var trust = Trust(accountId: "account-b");
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessAsync(
            request, trust, true, true, true, true, false, null, cancellationToken)).ConfigureAwait(true);
        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task ExactBindingReachesQueueOnce()
    {
        var submitCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator((request, decision, cancellationToken) =>
        {
            submitCalls++;
            return Task.FromResult(new GenerationProviderResponse(
                SubmissionDisposition.NotSubmitted,
                null,
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                "google-not-submitted"));
        });
        var coordinator = new GoogleGenerationBoundQueueCoordinator(queue);
        var request = new GenerationProviderRequest(
            "google-cloud-tts", "synthesize", "account-a", "idem", new byte[] { 1 }, "wav");

        var outcome = await coordinator.ProcessAsync(
            request,
            Trust(),
            accountAuthorized: true,
            projectAuthorized: true,
            capabilityCurrent: true,
            pricingCurrent: true,
            admissionCurrent: false,
            persistedIdempotencyKey: null,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(1, submitCalls);
        Assert.NotNull(outcome.Response);
    }

    private static GenerationCacheTrustContext Trust(string accountId = "account-a") => new(
        "google-cloud-tts", accountId, "project", "endpoint", "region", "synthesize", "model", "voice",
        "voice-fingerprint", "speech-plan", "en-US", "controls", "wav", "pcm16", "adapter", "compiler",
        "ast", "normalization", "pricing", "capability", "governance", "provider-feature", "account-capability");
}

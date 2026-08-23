using CloudScribe.Application.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleGenerationQueueCoordinatorTests
{
    [Fact]
    public async Task Authorized_request_reaches_submit_delegate_once()
    {
        var calls = 0;
        var coordinator = new GoogleGenerationQueueCoordinator((request, decision, cancellationToken) =>
        {
            calls++;
            return Task.FromResult(new GenerationProviderResponse(
                SubmissionDisposition.Accepted,
                "google-op",
                new byte[] { 1 },
                "audio/wav",
                null,
                "google-accepted"));
        });

        var request = new GenerationProviderRequest(
            "google-cloud-tts",
            "synthesize",
            "acct",
            "idem",
            new byte[] { 1, 2, 3 },
            "wav");

        var outcome = await coordinator.ProcessAsync(
            request,
            admissionCurrent: true,
            accountCredentialAvailable: true,
            pricingApproved: true,
            postCompileLimitsSatisfied: true,
            unresolvedPriorSubmission: false);

        Assert.Equal(1, calls);
        Assert.True(outcome.Decision.MaySubmit);
        Assert.NotNull(outcome.Response);
    }

    [Fact]
    public async Task Unresolved_submission_stays_reconciliation_only_and_never_resubmits()
    {
        var calls = 0;
        var coordinator = new GoogleGenerationQueueCoordinator((request, decision, cancellationToken) =>
        {
            calls++;
            throw new InvalidOperationException("submit must not be called");
        });

        var request = new GenerationProviderRequest(
            "google-cloud-tts",
            "synthesize",
            "acct",
            "idem",
            new byte[] { 1 },
            "wav");

        var outcome = await coordinator.ProcessAsync(
            request,
            admissionCurrent: true,
            accountCredentialAvailable: true,
            pricingApproved: true,
            postCompileLimitsSatisfied: true,
            unresolvedPriorSubmission: true);

        Assert.Equal(0, calls);
        Assert.True(outcome.RequiresReconciliation);
        Assert.Null(outcome.Response);
    }
}

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

        var request = Request();
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
        Assert.False(outcome.RequiresReconciliation);
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

        var outcome = await coordinator.ProcessAsync(
            Request(),
            admissionCurrent: true,
            accountCredentialAvailable: true,
            pricingApproved: true,
            postCompileLimitsSatisfied: true,
            unresolvedPriorSubmission: true);

        Assert.Equal(0, calls);
        Assert.True(outcome.RequiresReconciliation);
        Assert.Null(outcome.Response);
    }

    [Fact]
    public async Task Ambiguous_submit_response_becomes_reconciliation_required()
    {
        var coordinator = new GoogleGenerationQueueCoordinator((request, decision, cancellationToken) =>
            Task.FromResult(new GenerationProviderResponse(
                SubmissionDisposition.UnknownRequiresReconciliation,
                "google-ambiguous-op",
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                "google-transport-timeout")));

        var outcome = await coordinator.ProcessAsync(
            Request(),
            admissionCurrent: true,
            accountCredentialAvailable: true,
            pricingApproved: true,
            postCompileLimitsSatisfied: true,
            unresolvedPriorSubmission: false);

        Assert.True(outcome.RequiresReconciliation);
        Assert.Equal(SubmissionDisposition.UnknownRequiresReconciliation, outcome.Response!.Disposition);
    }

    private static GenerationProviderRequest Request() => new(
        "google-cloud-tts",
        "synthesize",
        "acct",
        "idem",
        new byte[] { 1, 2, 3 },
        "wav");
}

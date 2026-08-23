using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationQueueOutcome(
    GoogleGenerationExecutionDecision Decision,
    GenerationProviderResponse? Response)
{
    public bool RequiresReconciliation =>
        string.Equals(Decision.Reason, "google-reconciliation-required", StringComparison.Ordinal);
}

public sealed class GoogleGenerationQueueCoordinator
{
    private readonly Func<GenerationProviderRequest, GoogleGenerationExecutionDecision, CancellationToken, Task<GenerationProviderResponse>> _submit;

    public GoogleGenerationQueueCoordinator(
        Func<GenerationProviderRequest, GoogleGenerationExecutionDecision, CancellationToken, Task<GenerationProviderResponse>> submit)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
    }

    public async Task<GoogleGenerationQueueOutcome> ProcessAsync(
        GenerationProviderRequest request,
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        bool unresolvedPriorSubmission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = GoogleGenerationExecutionPolicy.Evaluate(
            admissionCurrent,
            accountCredentialAvailable,
            pricingApproved,
            postCompileLimitsSatisfied,
            unresolvedPriorSubmission);

        if (!decision.MayQueue)
            throw new InvalidOperationException($"Google generation is not queue-admissible: {decision.Reason}");

        if (!decision.MaySubmit)
        {
            if (!string.Equals(decision.Reason, "google-reconciliation-required", StringComparison.Ordinal))
                throw new InvalidOperationException($"Google generation was queued without a recognized non-submit state: {decision.Reason}");
            return new(decision, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var response = await _submit(request, decision, cancellationToken).ConfigureAwait(false);
        return new(decision, response);
    }
}

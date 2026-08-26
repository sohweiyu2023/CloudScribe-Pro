using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationQueueOutcome(
    GoogleGenerationExecutionDecision Decision,
    GenerationProviderResponse? Response)
{
    public bool RequiresReconciliation =>
        string.Equals(Decision.Reason, "google-reconciliation-required", StringComparison.Ordinal) ||
        Response?.Disposition == SubmissionDisposition.UnknownRequiresReconciliation;
}

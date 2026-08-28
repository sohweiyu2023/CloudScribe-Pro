namespace CloudScribe.Application.Generation;

public static class GoogleGenerationExecutionPolicy
{
    public static GoogleGenerationExecutionDecision Evaluate(
        bool admissionCurrent,
        bool accountCredentialAvailable,
        bool pricingApproved,
        bool postCompileLimitsSatisfied,
        bool unresolvedPriorSubmission)
    {
        if (!admissionCurrent) return new(false, false, "google-admission-stale");
        if (!accountCredentialAvailable) return new(false, false, "google-credential-unavailable");
        if (!pricingApproved) return new(false, false, "google-pricing-approval-required");
        if (!postCompileLimitsSatisfied) return new(false, false, "google-post-compile-limit-failed");
        if (unresolvedPriorSubmission) return new(true, false, "google-reconciliation-required");
        return new(true, true, "google-generation-authorized");
    }
}

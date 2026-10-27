namespace CloudScribe.Infrastructure.Generation;

public static class GoogleSubmissionSafety
{
    public static GoogleSubmissionDecision Decide(GoogleSubmissionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        attempt.Validate();

        if (attempt.ProviderAccepted && !attempt.ResponseObserved)
        {
            return new(GoogleSubmissionDisposition.ReconcileRequired, null,
                "Provider acceptance was observed without a terminal response; duplicate billable submission is forbidden until reconciliation.");
        }

        if (!attempt.ResponseObserved)
        {
            return new(GoogleSubmissionDisposition.ReconcileRequired, null,
                "Submission outcome is ambiguous; reconciliation is required before any retry.");
        }

        if (attempt.HttpStatusCode is 408 or 429 or >= 500 and <= 599)
            return new(GoogleSubmissionDisposition.ReconcileRequired, null,
                "Google synchronous synthesis has no verified safe idempotency contract; retain reservations and reconcile before retry.");

        if (attempt.HttpStatusCode is >= 400 and <= 499)
        {
            return new(GoogleSubmissionDisposition.Fail, null, "Non-rate-limit client response is not automatically retryable.");
        }

        return new(GoogleSubmissionDisposition.Submit, null, "No prior ambiguous or terminal provider failure blocks submission.");
    }
}

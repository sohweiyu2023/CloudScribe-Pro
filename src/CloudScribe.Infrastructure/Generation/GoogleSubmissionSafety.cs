namespace CloudScribe.Infrastructure.Generation;

public enum GoogleSubmissionDisposition
{
    Submit,
    RetrySafe,
    ReconcileRequired,
    Fail,
}

public sealed record GoogleSubmissionAttempt(
    string RequestIdentity,
    string IdempotencyKey,
    bool ProviderAccepted,
    bool ResponseObserved,
    bool ProviderSupportsSafeIdempotency,
    int? HttpStatusCode,
    TimeSpan? RetryAfter)
{
    public GoogleSubmissionAttempt Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RequestIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        if (RetryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryAfter));
        return this;
    }
}

public sealed record GoogleSubmissionDecision(
    GoogleSubmissionDisposition Disposition,
    TimeSpan? Delay,
    string Reason);

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

        if (attempt.HttpStatusCode is 429 or >= 500 and <= 599)
        {
            if (!attempt.ProviderSupportsSafeIdempotency)
            {
                return new(GoogleSubmissionDisposition.ReconcileRequired, null,
                    "Transient provider failure cannot be retried automatically without safe provider idempotency.");
            }

            var delay = attempt.RetryAfter ?? TimeSpan.FromSeconds(1);
            if (delay > TimeSpan.FromHours(1)) delay = TimeSpan.FromHours(1);
            return new(GoogleSubmissionDisposition.RetrySafe, delay, "Transient provider response permits bounded idempotent retry.");
        }

        if (attempt.HttpStatusCode is >= 400 and <= 499)
        {
            return new(GoogleSubmissionDisposition.Fail, null, "Non-rate-limit client response is not automatically retryable.");
        }

        return new(GoogleSubmissionDisposition.Submit, null, "No prior ambiguous or terminal provider failure blocks submission.");
    }
}

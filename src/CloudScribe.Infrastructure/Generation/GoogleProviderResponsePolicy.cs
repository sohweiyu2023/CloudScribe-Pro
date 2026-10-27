namespace CloudScribe.Infrastructure.Generation;

public static class GoogleProviderResponsePolicy
{
    public static GoogleProviderResponseDisposition Classify(
        int statusCode,
        TimeSpan? retryAfter,
        bool submissionOutcomeAmbiguous)
    {
        if (statusCode is < 100 or > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        if (retryAfter is { } boundedDelay && (boundedDelay < TimeSpan.Zero || boundedDelay > TimeSpan.FromHours(24)))
            throw new ArgumentOutOfRangeException(nameof(retryAfter));

        if (submissionOutcomeAmbiguous)
        {
            return new GoogleProviderResponseDisposition(
                GoogleRetryDisposition.ReconcileBeforeRetry,
                null,
                "Submission outcome is ambiguous; duplicate-cost safety requires reconciliation before retry.");
        }
        // Google synchronous synthesis has no enforced idempotency guarantee. Retry-After
        // is timing advice, not evidence that a timed-out/failed request was uncharged.
        if (statusCode is 408 or 429 or >= 500)
            return new GoogleProviderResponseDisposition(GoogleRetryDisposition.ReconcileBeforeRetry, null,
                "A dispatched response may still have incurred cost; reconcile before any new request.");

        return new GoogleProviderResponseDisposition(GoogleRetryDisposition.None, null, "Response is not automatically retryable by provider policy.");
    }
}

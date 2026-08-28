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
        if (statusCode == 429)
        {
            return retryAfter is { } delay
                ? new GoogleProviderResponseDisposition(GoogleRetryDisposition.RetryAfter, delay, "Provider rate limit supplied Retry-After.")
                : new GoogleProviderResponseDisposition(GoogleRetryDisposition.Backoff, null, "Provider rate limit requires bounded jittered backoff.");
        }
        if (statusCode is 408 or >= 500)
        {
            return retryAfter is { } delay
                ? new GoogleProviderResponseDisposition(GoogleRetryDisposition.RetryAfter, delay, "Transient provider failure supplied Retry-After.")
                : new GoogleProviderResponseDisposition(GoogleRetryDisposition.Backoff, null, "Transient provider failure requires bounded jittered backoff.");
        }

        return new GoogleProviderResponseDisposition(GoogleRetryDisposition.None, null, "Response is not automatically retryable by provider policy.");
    }
}

namespace CloudScribe.Infrastructure.Generation;

public enum GoogleRetryDisposition
{
    None,
    RetryAfter,
    Backoff,
    ReconcileBeforeRetry,
}

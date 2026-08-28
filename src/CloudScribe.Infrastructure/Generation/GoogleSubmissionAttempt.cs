namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSubmissionAttempt(
    string RequestIdentity,
    string IdempotencyKey,
    bool ProviderAccepted,
    bool ResponseObserved,
    bool ProviderSupportsSafeIdempotency,
    int? HttpStatusCode,
    TimeSpan? RetryAfter)
{
    public GoogleSubmissionAttempt Validate() => ValidateCore(RequestIdentity, IdempotencyKey, RetryAfter);

    private GoogleSubmissionAttempt ValidateCore(
        string requestIdentity,
        string idempotencyKey,
        TimeSpan? retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (retryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter));
        return this;
    }
}

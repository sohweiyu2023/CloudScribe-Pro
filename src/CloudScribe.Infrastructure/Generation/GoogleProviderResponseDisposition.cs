namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleProviderResponseDisposition(
    GoogleRetryDisposition Disposition,
    TimeSpan? RetryAfter,
    string Reason);

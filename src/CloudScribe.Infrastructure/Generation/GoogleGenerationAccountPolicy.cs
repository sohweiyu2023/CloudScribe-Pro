namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationAccount(
    string AccountId,
    string CredentialReferenceId,
    Uri Endpoint,
    string Region)
{
    public GoogleGenerationAccount Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CredentialReferenceId);
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(Region);
        if (!Endpoint.IsAbsoluteUri || !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Google generation endpoint must be an absolute HTTPS URI.", nameof(Endpoint));
        }
        if (!string.IsNullOrEmpty(Endpoint.UserInfo))
        {
            throw new ArgumentException("Provider endpoints must not embed credentials.", nameof(Endpoint));
        }
        if (!string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new ArgumentException("Provider endpoint identity must not contain query or fragment data.", nameof(Endpoint));
        }
        return this;
    }
}

public sealed record GoogleCapabilitySnapshot(
    string AccountId,
    string ProvenanceId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlySet<string> VoiceNames,
    IReadOnlySet<string> AudioEncodings,
    int MaximumCompiledPayloadBytes)
{
    public GoogleCapabilitySnapshot Validate(DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProvenanceId);
        ArgumentNullException.ThrowIfNull(VoiceNames);
        ArgumentNullException.ThrowIfNull(AudioEncodings);
        if (ObservedAtUtc > nowUtc)
        {
            throw new InvalidOperationException("Capability observation cannot be from the future.");
        }
        if (ExpiresAtUtc <= ObservedAtUtc)
        {
            throw new InvalidOperationException("Capability expiry must follow its observation time.");
        }
        if (MaximumCompiledPayloadBytes is < 256 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCompiledPayloadBytes));
        }
        return this;
    }

    public bool IsStale(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public void RequireSupported(string voiceName, string audioEncoding, int compiledPayloadBytes, DateTimeOffset nowUtc)
    {
        Validate(nowUtc);
        if (IsStale(nowUtc))
        {
            throw new InvalidOperationException("Google capability snapshot is stale and must be refreshed before billable submission.");
        }
        if (!VoiceNames.Contains(voiceName))
        {
            throw new InvalidOperationException("Selected Google voice is not present in the current capability snapshot.");
        }
        if (!AudioEncodings.Contains(audioEncoding))
        {
            throw new InvalidOperationException("Selected Google audio encoding is not present in the current capability snapshot.");
        }
        if (compiledPayloadBytes > MaximumCompiledPayloadBytes)
        {
            throw new InvalidOperationException("Compiled Google payload exceeds the current capability snapshot limit.");
        }
    }
}

public enum GoogleRetryDisposition
{
    None,
    RetryAfter,
    Backoff,
    ReconcileBeforeRetry,
}

public sealed record GoogleProviderResponseDisposition(
    GoogleRetryDisposition Disposition,
    TimeSpan? RetryAfter,
    string Reason);

public static class GoogleProviderResponsePolicy
{
    public static GoogleProviderResponseDisposition Classify(
        int statusCode,
        TimeSpan? retryAfter,
        bool submissionOutcomeAmbiguous)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }
        if (retryAfter < TimeSpan.Zero || retryAfter > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

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

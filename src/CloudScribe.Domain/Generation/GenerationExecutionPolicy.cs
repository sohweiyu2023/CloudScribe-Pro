using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Generation;

public enum SubmissionDisposition
{
    NotSubmitted,
    Accepted,
    RejectedSafeToRetry,
    UnknownRequiresReconciliation,
}

public sealed record GenerationRetryDecision(
    bool MayRetryAutomatically,
    TimeSpan Delay,
    string Reason)
{
    public static GenerationRetryDecision Blocked(string reason) => new(false, TimeSpan.Zero, reason);
}

public sealed class GenerationExecutionPolicy
{
    public GenerationExecutionPolicy(
        int maximumAttempts,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        int maximumConcurrentRequests)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (initialBackoff <= TimeSpan.Zero || maximumBackoff < initialBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff));
        }

        if (maximumConcurrentRequests < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentRequests));
        }

        MaximumAttempts = maximumAttempts;
        InitialBackoff = initialBackoff;
        MaximumBackoff = maximumBackoff;
        MaximumConcurrentRequests = maximumConcurrentRequests;
    }

    public int MaximumAttempts { get; }

    public TimeSpan InitialBackoff { get; }

    public TimeSpan MaximumBackoff { get; }

    public int MaximumConcurrentRequests { get; }

    public GenerationRetryDecision DecideRetry(
        GenerationJobState state,
        SubmissionDisposition disposition,
        int completedAttempts,
        TimeSpan? retryAfter,
        ulong deterministicJitterSeed)
    {
        if (completedAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }

        if (GenerationJobStateMachine.RequiresReconciliationBeforeAutomaticRetry(state) ||
            disposition == SubmissionDisposition.UnknownRequiresReconciliation)
        {
            return GenerationRetryDecision.Blocked("Submission outcome is ambiguous and must be reconciled before any automatic retry.");
        }

        if (disposition == SubmissionDisposition.Accepted)
        {
            return GenerationRetryDecision.Blocked("An accepted billable submission must not be duplicated automatically.");
        }

        if (completedAttempts >= MaximumAttempts)
        {
            return GenerationRetryDecision.Blocked("Maximum automatic attempt count reached.");
        }

        if (retryAfter is { } providerDelay && providerDelay > TimeSpan.Zero)
        {
            return new GenerationRetryDecision(true, Clamp(providerDelay), "Provider Retry-After honored.");
        }

        var exponent = Math.Max(0, completedAttempts);
        var scale = Math.Pow(2, Math.Min(exponent, 30));
        var rawTicks = InitialBackoff.Ticks * scale;
        var cappedTicks = Math.Min(rawTicks, MaximumBackoff.Ticks);
        var jitterFraction = (deterministicJitterSeed % 1001UL) / 1000.0;
        var jitterMultiplier = 0.8 + (0.4 * jitterFraction);
        var jitteredTicks = Math.Max(1L, (long)(cappedTicks * jitterMultiplier));

        return new GenerationRetryDecision(
            true,
            Clamp(TimeSpan.FromTicks(jitteredTicks)),
            "Deterministic bounded exponential backoff.");
    }

    private TimeSpan Clamp(TimeSpan value) => value > MaximumBackoff ? MaximumBackoff : value;
}

public sealed record ContentAddressedSegmentKey(string Sha256)
{
    public static ContentAddressedSegmentKey Create(
        ReadOnlySpan<byte> compiledPayload,
        string providerStableId,
        string operationStableId,
        string voiceStableId,
        string compilationProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilationProfileId);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hasher, "cloudscribe-segment-cache-v1\n");
        AppendUtf8(hasher, providerStableId);
        AppendUtf8(hasher, "\n");
        AppendUtf8(hasher, operationStableId);
        AppendUtf8(hasher, "\n");
        AppendUtf8(hasher, voiceStableId);
        AppendUtf8(hasher, "\n");
        AppendUtf8(hasher, compilationProfileId);
        AppendUtf8(hasher, "\n");
        hasher.AppendData(compiledPayload);
        return new ContentAddressedSegmentKey(Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    private static void AppendUtf8(IncrementalHash hasher, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hasher.AppendData(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }
}

public sealed class GenerationSubmissionRecord
{
    public GenerationSubmissionRecord(
        string idempotencyKey,
        SubmissionDisposition disposition,
        string? providerRequestId,
        long recordedAtUnixMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (disposition == SubmissionDisposition.Accepted && string.IsNullOrWhiteSpace(providerRequestId))
        {
            throw new ArgumentException("Accepted submissions require a provider request identifier.", nameof(providerRequestId));
        }

        IdempotencyKey = idempotencyKey;
        Disposition = disposition;
        ProviderRequestId = providerRequestId;
        RecordedAtUnixMilliseconds = recordedAtUnixMilliseconds;
    }

    public string IdempotencyKey { get; }

    public SubmissionDisposition Disposition { get; }

    public string? ProviderRequestId { get; }

    public long RecordedAtUnixMilliseconds { get; }

    public bool RequiresReconciliation => Disposition == SubmissionDisposition.UnknownRequiresReconciliation;
}

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

public sealed record GenerationExecutionPolicy(
    int MaximumAttempts,
    TimeSpan InitialBackoff,
    TimeSpan MaximumBackoff,
    int MaximumConcurrentRequests)
{
    public GenerationExecutionPolicy
    {
        if (MaximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        }

        if (InitialBackoff <= TimeSpan.Zero || MaximumBackoff < InitialBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialBackoff));
        }

        if (MaximumConcurrentRequests < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentRequests));
        }
    }

    public GenerationRetryDecision DecideRetry(
        GenerationJobState state,
        SubmissionDisposition disposition,
        int completedAttempts,
        TimeSpan? retryAfter,
        ulong deterministicJitterSeed)
    {
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

public sealed record GenerationSubmissionRecord(
    string IdempotencyKey,
    SubmissionDisposition Disposition,
    string? ProviderRequestId,
    long RecordedAtUnixMilliseconds)
{
    public GenerationSubmissionRecord
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        if (Disposition == SubmissionDisposition.Accepted && string.IsNullOrWhiteSpace(ProviderRequestId))
        {
            throw new ArgumentException("Accepted submissions require a provider request identifier.", nameof(ProviderRequestId));
        }
    }

    public bool RequiresReconciliation => Disposition == SubmissionDisposition.UnknownRequiresReconciliation;
}

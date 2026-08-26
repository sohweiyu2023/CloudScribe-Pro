namespace CloudScribe.Domain.Generation;

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

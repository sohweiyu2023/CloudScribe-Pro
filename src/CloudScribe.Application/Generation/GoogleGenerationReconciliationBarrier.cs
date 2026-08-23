namespace CloudScribe.Application.Generation;

public static class GoogleGenerationReconciliationBarrier
{
    public static void RequireNoDuplicateSubmission(
        bool unresolvedPriorSubmission,
        string idempotencyKey,
        string? persistedIdempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (unresolvedPriorSubmission)
            throw new InvalidOperationException("Google generation is reconciliation-gated; a prior ambiguous submission forbids duplicate billable submission.");

        if (!string.IsNullOrWhiteSpace(persistedIdempotencyKey) &&
            !string.Equals(idempotencyKey, persistedIdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Google generation idempotency identity changed across persisted queue state.");
    }
}

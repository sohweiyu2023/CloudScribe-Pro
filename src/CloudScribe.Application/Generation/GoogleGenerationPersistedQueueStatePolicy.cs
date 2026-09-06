namespace CloudScribe.Application.Generation;

public static class GoogleGenerationPersistedQueueStatePolicy
{
    public static void RequireCompatible(
        GoogleGenerationPersistedQueueState persisted,
        string currentAccountId,
        string currentOperationStableId,
        string currentIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        persisted.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentOperationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentIdempotencyKey);

        if (!string.Equals(currentAccountId, currentAccountId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(currentOperationStableId, currentOperationStableId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(currentIdempotencyKey, currentIdempotencyKey.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Current Google queue identity must be canonical before compatibility evaluation.");

        if (!string.Equals(persisted.AccountId, currentAccountId, StringComparison.Ordinal) ||
            !string.Equals(persisted.OperationStableId, currentOperationStableId, StringComparison.Ordinal) ||
            !string.Equals(persisted.IdempotencyKey, currentIdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted Google queue identity differs from the current admitted request identity.");

        if (persisted.UnresolvedSubmission)
            throw new InvalidOperationException("Persisted Google submission is unresolved; duplicate billable submission is forbidden until reconciliation completes.");
    }
}

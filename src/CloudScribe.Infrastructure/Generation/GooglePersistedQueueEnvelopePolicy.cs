namespace CloudScribe.Infrastructure.Generation;

public static class GooglePersistedQueueEnvelopePolicy
{
    public static void RequireCompatible(
        GooglePersistedQueueEnvelope persisted,
        string currentAccountId,
        string currentOperationStableId,
        string currentIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        persisted.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentOperationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentIdempotencyKey);

        if (!string.Equals(persisted.AccountId, currentAccountId, StringComparison.Ordinal) ||
            !string.Equals(persisted.OperationStableId, currentOperationStableId, StringComparison.Ordinal) ||
            !string.Equals(persisted.IdempotencyKey, currentIdempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted Google queue identity differs from the current admitted generation identity.");
        }

        if (persisted.UnresolvedSubmission)
        {
            throw new InvalidOperationException("Persisted Google submission is unresolved; duplicate billable submission is forbidden until reconciliation completes.");
        }
    }
}

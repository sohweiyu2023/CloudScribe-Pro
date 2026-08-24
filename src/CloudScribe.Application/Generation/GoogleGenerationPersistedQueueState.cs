namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationPersistedQueueState(
    string AccountId,
    string OperationStableId,
    string IdempotencyKey,
    bool UnresolvedSubmission,
    string? ProviderRequestId)
{
    public GoogleGenerationPersistedQueueState Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey);
        if (UnresolvedSubmission && string.IsNullOrWhiteSpace(ProviderRequestId))
            throw new InvalidOperationException("An unresolved Google submission must retain its provider request identity for reconciliation.");
        return this;
    }
}

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

        if (!string.Equals(persisted.AccountId, currentAccountId, StringComparison.Ordinal) ||
            !string.Equals(persisted.OperationStableId, currentOperationStableId, StringComparison.Ordinal) ||
            !string.Equals(persisted.IdempotencyKey, currentIdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted Google queue identity differs from the current admitted request identity.");

        if (persisted.UnresolvedSubmission)
            throw new InvalidOperationException("Persisted Google submission is unresolved; duplicate billable submission is forbidden until reconciliation completes.");
    }
}

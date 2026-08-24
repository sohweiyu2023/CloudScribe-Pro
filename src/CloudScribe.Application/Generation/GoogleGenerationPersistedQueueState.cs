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
        RequireCanonical(AccountId, nameof(AccountId));
        RequireCanonical(OperationStableId, nameof(OperationStableId));
        RequireCanonical(IdempotencyKey, nameof(IdempotencyKey));
        if (ProviderRequestId is not null)
            RequireCanonical(ProviderRequestId, nameof(ProviderRequestId));
        if (UnresolvedSubmission && string.IsNullOrWhiteSpace(ProviderRequestId))
            throw new InvalidOperationException("An unresolved Google submission must retain its provider request identity for reconciliation.");
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Persisted Google queue identity '{parameterName}' must be canonical and contain no leading or trailing whitespace.");
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new InvalidOperationException($"Persisted Google queue identity '{parameterName}' contains forbidden control characters.");
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

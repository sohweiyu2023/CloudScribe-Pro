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

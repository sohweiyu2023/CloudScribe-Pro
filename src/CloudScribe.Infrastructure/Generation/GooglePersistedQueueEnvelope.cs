namespace CloudScribe.Infrastructure.Generation;

public sealed record GooglePersistedQueueEnvelope(
    string AccountId,
    string OperationStableId,
    string IdempotencyKey,
    bool UnresolvedSubmission,
    string? PersistedProviderRequestId)
{
    public GooglePersistedQueueEnvelope Validate() => Validate(
        AccountId,
        OperationStableId,
        IdempotencyKey,
        UnresolvedSubmission,
        PersistedProviderRequestId);

    private GooglePersistedQueueEnvelope Validate(
        string accountId,
        string operationStableId,
        string idempotencyKey,
        bool unresolvedSubmission,
        string? persistedProviderRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (unresolvedSubmission && string.IsNullOrWhiteSpace(persistedProviderRequestId))
        {
            throw new InvalidOperationException("An unresolved Google submission must retain its persisted provider request identity.");
        }
        return this;
    }
}

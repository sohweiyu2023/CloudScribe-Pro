namespace CloudScribe.Application.Generation;

public static class GoogleGenerationPersistedQueueTransitionPolicy
{
    public static GoogleGenerationPersistedQueueState ValidateTransition(
        GoogleGenerationPersistedQueueState previous,
        GoogleGenerationPersistedQueueState next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        previous.Validate();
        next.Validate();

        if (!string.Equals(previous.AccountId, next.AccountId, StringComparison.Ordinal) ||
            !string.Equals(previous.OperationStableId, next.OperationStableId, StringComparison.Ordinal) ||
            !string.Equals(previous.IdempotencyKey, next.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted Google queue identity is immutable across state transitions.");

        if (previous.UnresolvedSubmission)
        {
            if (string.IsNullOrWhiteSpace(previous.ProviderRequestId))
                throw new InvalidOperationException("Unresolved persisted Google state is missing provider reconciliation identity.");

            if (!next.UnresolvedSubmission && string.IsNullOrWhiteSpace(next.ProviderRequestId))
                throw new InvalidOperationException("Resolving an ambiguous Google submission cannot discard the provider request identity.");

            if (!string.Equals(previous.ProviderRequestId, next.ProviderRequestId, StringComparison.Ordinal))
                throw new InvalidOperationException("Provider reconciliation identity cannot drift while resolving an ambiguous Google submission.");
        }

        if (!previous.UnresolvedSubmission && next.UnresolvedSubmission && string.IsNullOrWhiteSpace(next.ProviderRequestId))
            throw new InvalidOperationException("Entering unresolved submission state requires the persisted provider request identity.");

        return next;
    }
}

namespace CloudScribe.Application.Generation;

public static class GoogleGenerationPersistedQueueTransitionPolicy
{
    public static GoogleGenerationPersistedQueueState ValidateTransition(
        GoogleGenerationPersistedQueueState previous,
        GoogleGenerationPersistedQueueState next) =>
        ValidateTransition(previous, next, GoogleGenerationReconciliationResolutionEvidence.None);

    public static GoogleGenerationPersistedQueueState ValidateTransition(
        GoogleGenerationPersistedQueueState previous,
        GoogleGenerationPersistedQueueState next,
        GoogleGenerationReconciliationResolutionEvidence resolutionEvidence)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        previous.Validate();
        next.Validate();

        if (!Enum.IsDefined(resolutionEvidence))
            throw new ArgumentOutOfRangeException(nameof(resolutionEvidence));

        if (!string.Equals(previous.AccountId, next.AccountId, StringComparison.Ordinal) ||
            !string.Equals(previous.OperationStableId, next.OperationStableId, StringComparison.Ordinal) ||
            !string.Equals(previous.IdempotencyKey, next.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted Google queue identity is immutable across state transitions.");

        var previousProviderRequestId = NormalizeOptional(previous.ProviderRequestId);
        var nextProviderRequestId = NormalizeOptional(next.ProviderRequestId);

        if (previousProviderRequestId is not null &&
            !string.Equals(previousProviderRequestId, nextProviderRequestId, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted provider request identity is append-only and cannot be changed or discarded once observed.");

        if (previous.UnresolvedSubmission)
        {
            if (previousProviderRequestId is null)
                throw new InvalidOperationException("Unresolved persisted Google state is missing provider reconciliation identity.");

            if (!next.UnresolvedSubmission)
            {
                if (nextProviderRequestId is null)
                    throw new InvalidOperationException("Resolving an ambiguous Google submission cannot discard the provider request identity.");
                if (resolutionEvidence == GoogleGenerationReconciliationResolutionEvidence.None)
                    throw new InvalidOperationException("Resolving an ambiguous Google submission requires explicit provider-terminal or durable-receipt reconciliation evidence.");
            }
            else if (resolutionEvidence != GoogleGenerationReconciliationResolutionEvidence.None)
            {
                throw new InvalidOperationException("Google reconciliation resolution evidence is only valid when clearing an unresolved submission.");
            }
        }
        else
        {
            if (resolutionEvidence != GoogleGenerationReconciliationResolutionEvidence.None)
                throw new InvalidOperationException("Google reconciliation resolution evidence is only valid when clearing an unresolved submission.");

            if (next.UnresolvedSubmission && nextProviderRequestId is null)
                throw new InvalidOperationException("Entering unresolved submission state requires the persisted provider request identity.");
        }

        return next;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

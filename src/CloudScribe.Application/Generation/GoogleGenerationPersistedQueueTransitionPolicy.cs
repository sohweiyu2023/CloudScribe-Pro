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

        if (previous.SubmissionAttempted && !next.SubmissionAttempted)
            throw new InvalidOperationException("A submission-attempt barrier cannot be reset.");
        if (previous.LocalAttemptId is not null && !string.Equals(previous.LocalAttemptId, next.LocalAttemptId, StringComparison.Ordinal))
            throw new InvalidOperationException("An owned local attempt identity cannot be replaced.");
        var previousProviderRequestId = NormalizeOptional(previous.ProviderRequestId);
        var nextProviderRequestId = NormalizeOptional(next.ProviderRequestId);

        if (previousProviderRequestId is not null &&
            !string.Equals(previousProviderRequestId, nextProviderRequestId, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted provider request identity is append-only and cannot be changed or discarded once observed.");

        if (previous.UnresolvedSubmission)
        {
            if (!next.UnresolvedSubmission)
            {
                if (previous.LocalAttemptId is not null && !GoogleGenerationOutcomeDiagnostic.IsConfirmedTerminal(next.DiagnosticCode))
                    throw new InvalidOperationException("An uncertain owned outcome cannot be marked reconciled.");
                if (previousProviderRequestId is null && (previous.LocalAttemptId is null || next.DiagnosticCode is null))
                    throw new InvalidOperationException("Resolving a no-provider-ID submission requires its owned local claim and terminal outcome metadata.");
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

            if (next.UnresolvedSubmission && nextProviderRequestId is null && next.LocalAttemptId is null)
                throw new InvalidOperationException("Entering unresolved state requires a genuine local dispatch claim or provider identity.");
        }

        return next;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

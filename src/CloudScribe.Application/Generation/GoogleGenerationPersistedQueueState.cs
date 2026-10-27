using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationPersistedQueueState(
    string AccountId,
    string OperationStableId,
    string IdempotencyKey,
    bool UnresolvedSubmission,
    string? ProviderRequestId,
    bool SubmissionAttempted = false,
    string? LocalAttemptId = null,
    string? DiagnosticCode = null,
    string? PayloadSha256 = null,
    int? PayloadBytes = null,
    long? OutcomeObservedAtUnixMilliseconds = null,
    GoogleErrorStatus ErrorStatus = GoogleErrorStatus.NotProvided)
{
    public GoogleGenerationPersistedQueueState Validate()
    {
        RequireCanonical(AccountId, nameof(AccountId));
        RequireCanonical(OperationStableId, nameof(OperationStableId));
        RequireCanonical(IdempotencyKey, nameof(IdempotencyKey));
        if (ProviderRequestId is not null)
            RequireCanonical(ProviderRequestId, nameof(ProviderRequestId));
        if (!Enum.IsDefined(ErrorStatus)) throw new InvalidOperationException("Unknown error status metadata.");
        // A dispatched synchronous request may never yield a provider ID. Its exact local
        // identity must remain blocked without inventing a provider reconciliation receipt.
        if (LocalAttemptId is not null && (!SubmissionAttempted || !Guid.TryParseExact(LocalAttemptId, "N", out _)))
            throw new InvalidOperationException("Local attempt identity must be a genuine local GUID owned by a dispatch claim.");
        if (DiagnosticCode is not null && !string.Equals(GoogleGenerationOutcomeDiagnostic.Sanitize(DiagnosticCode), DiagnosticCode, StringComparison.Ordinal))
            throw new InvalidOperationException("Only allowlisted outcome metadata may be persisted.");
        if (PayloadSha256 is not null && (PayloadSha256.Length != 64 || PayloadSha256.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidOperationException("Compiled payload identity must be a SHA256 hash.");
        if (PayloadBytes is < 0 || OutcomeObservedAtUnixMilliseconds is < 0)
            throw new InvalidOperationException("Outcome metadata cannot have negative quantities.");
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

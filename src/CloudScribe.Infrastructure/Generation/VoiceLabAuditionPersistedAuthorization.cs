using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record VoiceLabAuditionPersistedAuthorization(
    VoiceLabCatalogSelection Selection,
    string CredentialReferenceId,
    string PricingEvidenceId,
    string SpendAuthorizationId,
    long AccountRevision,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public VoiceLabAuditionAuthorizationEvidence ToCurrentEvidence(DateTimeOffset nowUtc)
    {
        if (Selection is null)
            throw new InvalidOperationException("Persisted Voice Lab audition authorization requires a catalog selection.");
        Selection.Validate();
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(PricingEvidenceId, nameof(PricingEvidenceId));
        RequireCanonical(SpendAuthorizationId, nameof(SpendAuthorizationId));
        if (AccountRevision < 1)
            throw new InvalidOperationException("Persisted Voice Lab audition authorization requires a positive account revision.");
        if (CapturedAtUtc.Offset != TimeSpan.Zero || ExpiresAtUtc.Offset != TimeSpan.Zero || nowUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Voice Lab audition authorization timestamps must be UTC.");
        if (ExpiresAtUtc <= CapturedAtUtc || CapturedAtUtc > nowUtc || nowUtc >= ExpiresAtUtc)
            throw new InvalidOperationException("Voice Lab audition authorization is not current.");

        return new VoiceLabAuditionAuthorizationEvidence(
            Selection,
            CredentialReferenceId,
            PricingEvidenceId,
            SpendAuthorizationId,
            PricingCurrent: true,
            SpendApproved: true,
            AccountRevision);
    }

    private static void RequireCanonical(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Persisted Voice Lab audition authorization '{propertyName}' is required.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Persisted Voice Lab audition authorization '{propertyName}' must be canonical.");
        }
    }
}

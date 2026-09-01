using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record VoiceLabAuditionAuthorizationEvidence(
    VoiceLabCatalogSelection Selection,
    string CredentialReferenceId,
    string PricingEvidenceId,
    string SpendAuthorizationId,
    bool PricingCurrent,
    bool SpendApproved,
    long AccountRevision = 1)
{
    public VoiceLabAuditionAuthorizationEvidence Validate()
    {
        (Selection ?? throw new InvalidOperationException("Voice Lab audition authorization requires a catalog selection.")).Validate();
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(PricingEvidenceId, nameof(PricingEvidenceId));
        RequireCanonical(SpendAuthorizationId, nameof(SpendAuthorizationId));
        if (AccountRevision < 1)
            throw new InvalidOperationException("Voice Lab audition authorization requires a positive provider account revision.");
        if (!PricingCurrent)
            throw new InvalidOperationException("Voice Lab audition authorization requires current pricing evidence.");
        if (!SpendApproved)
            throw new InvalidOperationException("Voice Lab audition authorization requires explicit current spend approval.");
        return this;
    }

    public void EnsureStillAuthorized(VoiceLabAuditionAuthorizationEvidence current)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        Validate();

        if (!Equals(current))
            throw new InvalidOperationException("Voice Lab audition authorization evidence changed after approval; the audition must be re-authorized.");
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Voice Lab audition authorization '{parameterName}' must be canonical.");
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new InvalidOperationException($"Voice Lab audition authorization '{parameterName}' contains forbidden control characters.");
    }
}

using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record VoiceLabAuditionAuthorizationEvidence(
    VoiceLabCatalogSelection Selection,
    string CredentialReferenceId,
    string PricingEvidenceId,
    string SpendAuthorizationId,
    bool PricingCurrent,
    bool SpendApproved,
    long AccountRevision = 1,
    Uri? EndpointOrigin = null)
{
    public VoiceLabAuditionAuthorizationEvidence Validate()
    {
        (Selection ?? throw new InvalidOperationException("Voice Lab audition authorization requires a catalog selection.")).Validate();
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(PricingEvidenceId, nameof(PricingEvidenceId));
        RequireCanonical(SpendAuthorizationId, nameof(SpendAuthorizationId));
        if (AccountRevision < 1)
            throw new InvalidOperationException("Voice Lab audition authorization requires a persisted provider account revision.");
        ValidateEndpointOrigin(EndpointOrigin);
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

    private static void ValidateEndpointOrigin(Uri? endpointOrigin)
    {
        if (endpointOrigin is null)
            throw new InvalidOperationException("Voice Lab audition authorization requires an explicit persisted endpoint origin.");
        if (!endpointOrigin.IsAbsoluteUri || !string.Equals(endpointOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Voice Lab audition authorization endpoint origin must be an absolute HTTPS URI.");
        if (!string.IsNullOrEmpty(endpointOrigin.UserInfo) || !string.IsNullOrEmpty(endpointOrigin.Query) || !string.IsNullOrEmpty(endpointOrigin.Fragment))
            throw new InvalidOperationException("Voice Lab audition authorization endpoint origin must not contain credentials, query, or fragment components.");

        Uri normalized = new(endpointOrigin.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        if (endpointOrigin != normalized)
            throw new InvalidOperationException("Voice Lab audition authorization endpoint origin must contain only scheme and authority.");
    }
}

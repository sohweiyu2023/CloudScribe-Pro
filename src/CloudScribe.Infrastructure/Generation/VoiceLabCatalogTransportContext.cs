using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record VoiceLabCatalogTransportContext
{
    public VoiceLabCatalogTransportContext(
        VoiceLabCatalogQuery query,
        string credentialReferenceId,
        string capabilityEvidenceId,
        Uri endpointOrigin)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        CredentialReferenceId = RequireCanonical(credentialReferenceId, nameof(credentialReferenceId));
        CapabilityEvidenceId = RequireCanonical(capabilityEvidenceId, nameof(capabilityEvidenceId));
        EndpointOrigin = RequireExplicitHttpsOrigin(endpointOrigin);
    }

    public VoiceLabCatalogQuery Query { get; }

    public string CredentialReferenceId { get; }

    public string CapabilityEvidenceId { get; }

    public Uri EndpointOrigin { get; }

    private static string RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Voice Lab catalog transport context '{parameterName}' is not canonical.");
        }

        return value;
    }

    private static Uri RequireExplicitHttpsOrigin(Uri endpointOrigin)
    {
        ArgumentNullException.ThrowIfNull(endpointOrigin);
        if (!endpointOrigin.IsAbsoluteUri ||
            !string.Equals(endpointOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpointOrigin.Host) ||
            !string.IsNullOrEmpty(endpointOrigin.UserInfo) ||
            !string.IsNullOrEmpty(endpointOrigin.Query) ||
            !string.IsNullOrEmpty(endpointOrigin.Fragment) ||
            (endpointOrigin.AbsolutePath.Length > 0 && !string.Equals(endpointOrigin.AbsolutePath, "/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Voice Lab catalog transport requires an explicit HTTPS scheme-and-authority endpoint origin.");
        }

        return endpointOrigin;
    }
}

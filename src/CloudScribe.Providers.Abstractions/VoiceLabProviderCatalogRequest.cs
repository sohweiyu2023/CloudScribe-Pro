namespace CloudScribe.Providers.Abstractions;

public sealed record VoiceLabProviderCatalogRequest(
    string ProjectStableId,
    string CredentialReferenceId,
    string CapabilityEvidenceId,
    Uri EndpointOrigin,
    string? SearchText,
    string? Locale,
    bool IncludePrivateVoices)
{
    public VoiceLabProviderCatalogRequest Validate()
    {
        RequireCanonical(ProjectStableId, nameof(ProjectStableId));
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(CapabilityEvidenceId, nameof(CapabilityEvidenceId));
        RequireHttpsOrigin(EndpointOrigin, nameof(EndpointOrigin));

        if (SearchText is { Length: > 256 })
            throw new InvalidOperationException("Voice Lab provider search text exceeds the bounded length.");
        if (Locale is { Length: > 32 })
            throw new InvalidOperationException("Voice Lab provider locale exceeds the bounded length.");

        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Voice Lab provider identity '{parameterName}' must be canonical.");
        }
    }

    private static void RequireHttpsOrigin(Uri endpointOrigin, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(endpointOrigin, parameterName);
        if (!endpointOrigin.IsAbsoluteUri ||
            !string.Equals(endpointOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpointOrigin.UserInfo) ||
            !string.Equals(endpointOrigin.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpointOrigin.Query) ||
            !string.IsNullOrEmpty(endpointOrigin.Fragment))
        {
            throw new InvalidOperationException("Voice Lab provider endpoint must be an explicit HTTPS origin.");
        }
    }
}

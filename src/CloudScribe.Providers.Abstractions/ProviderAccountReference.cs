namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderAccountReference
{
    public ProviderAccountReference(
        string providerStableId,
        string accountId,
        string displayName,
        CredentialReference? credentialReference,
        string? endpointId = null,
        string? regionId = null,
        Uri? endpointOrigin = null)
    {
        ProviderStableId = ProviderIdentifierRules.NormalizeStableId(providerStableId, nameof(providerStableId));
        AccountId = ProviderIdentifierRules.NormalizeStableId(accountId, nameof(accountId));
        DisplayName = ProviderIdentifierRules.NormalizeDisplayName(displayName, nameof(displayName));
        CredentialReference = credentialReference;
        EndpointId = NormalizeOptionalStableId(endpointId, nameof(endpointId));
        RegionId = NormalizeOptionalStableId(regionId, nameof(regionId));
        EndpointOrigin = NormalizeEndpointOrigin(endpointOrigin, nameof(endpointOrigin));
    }

    public string ProviderStableId { get; }
    public string AccountId { get; }
    public string DisplayName { get; }
    public CredentialReference? CredentialReference { get; }
    public string? EndpointId { get; }
    public string? RegionId { get; }
    public Uri? EndpointOrigin { get; }

    private static string? NormalizeOptionalStableId(string? value, string parameterName) =>
        value is null ? null : ProviderIdentifierRules.NormalizeStableId(value, parameterName);

    private static Uri? NormalizeEndpointOrigin(Uri? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (!value.IsAbsoluteUri || !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Provider endpoint origin must be an absolute HTTPS URI.", parameterName);
        }

        if (!string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Query) || !string.IsNullOrEmpty(value.Fragment))
        {
            throw new ArgumentException("Provider endpoint origin must not contain credentials, query, or fragment components.", parameterName);
        }

        return new Uri(value.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }
}

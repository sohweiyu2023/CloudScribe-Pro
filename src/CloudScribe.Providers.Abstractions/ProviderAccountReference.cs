namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderAccountReference
{
    public ProviderAccountReference(
        string providerStableId,
        string accountId,
        string displayName,
        CredentialReference? credentialReference,
        string? endpointId = null,
        string? regionId = null)
    {
        ProviderStableId = ProviderIdentifierRules.NormalizeStableId(providerStableId, nameof(providerStableId));
        AccountId = ProviderIdentifierRules.NormalizeStableId(accountId, nameof(accountId));
        DisplayName = ProviderIdentifierRules.NormalizeDisplayName(displayName, nameof(displayName));
        CredentialReference = credentialReference;
        EndpointId = NormalizeOptionalStableId(endpointId, nameof(endpointId));
        RegionId = NormalizeOptionalStableId(regionId, nameof(regionId));
    }

    public string ProviderStableId { get; }
    public string AccountId { get; }
    public string DisplayName { get; }
    public CredentialReference? CredentialReference { get; }
    public string? EndpointId { get; }
    public string? RegionId { get; }

    private static string? NormalizeOptionalStableId(string? value, string parameterName) =>
        value is null ? null : ProviderIdentifierRules.NormalizeStableId(value, parameterName);
}

namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderDescriptor
{
    public ProviderDescriptor(
        string stableId,
        string displayName,
        bool requiresNetwork,
        bool requiresCredentials)
    {
        StableId = ProviderIdentifierRules.NormalizeStableId(stableId, nameof(stableId));
        DisplayName = ProviderIdentifierRules.NormalizeDisplayName(displayName, nameof(displayName));
        RequiresNetwork = requiresNetwork;
        RequiresCredentials = requiresCredentials;
    }

    public string StableId { get; }
    public string DisplayName { get; }
    public bool RequiresNetwork { get; }
    public bool RequiresCredentials { get; }
}

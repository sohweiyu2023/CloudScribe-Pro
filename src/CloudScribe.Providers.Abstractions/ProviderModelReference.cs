namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderModelReference
{
    public ProviderModelReference(
        string stableId,
        string exactApiAlias,
        ProviderLifecycleState lifecycleState,
        string? resolvedVersion = null)
    {
        StableId = ProviderIdentifierRules.NormalizeStableId(stableId, nameof(stableId), maximumLength: 96);
        ExactApiAlias = ProviderIdentifierRules.NormalizeDisplayName(exactApiAlias, nameof(exactApiAlias), maximumLength: 256);
        LifecycleState = lifecycleState;
        ResolvedVersion = resolvedVersion is null
            ? null
            : ProviderIdentifierRules.NormalizeDisplayName(resolvedVersion, nameof(resolvedVersion), maximumLength: 128);
    }

    public string StableId { get; }
    public string ExactApiAlias { get; }
    public ProviderLifecycleState LifecycleState { get; }
    public string? ResolvedVersion { get; }
}

namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderOperationReference
{
    public ProviderOperationReference(string stableId, ProviderLifecycleState lifecycleState)
    {
        StableId = ProviderIdentifierRules.NormalizeStableId(stableId, nameof(stableId), maximumLength: 96);
        LifecycleState = lifecycleState;
    }

    public string StableId { get; }
    public ProviderLifecycleState LifecycleState { get; }
}

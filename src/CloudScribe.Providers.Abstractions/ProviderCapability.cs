namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderCapability
{
    public ProviderCapability(
        string capabilityId,
        ProviderCapabilityState state,
        ProviderLifecycleState lifecycleState,
        string? disabledReason = null)
    {
        CapabilityId = ProviderIdentifierRules.NormalizeStableId(capabilityId, nameof(capabilityId), maximumLength: 96);
        State = state;
        LifecycleState = lifecycleState;
        if (state == ProviderCapabilityState.Supported && disabledReason is not null)
        {
            throw new ArgumentException("A supported capability cannot carry a disabled reason.", nameof(disabledReason));
        }
        if (state != ProviderCapabilityState.Supported && string.IsNullOrWhiteSpace(disabledReason))
        {
            throw new ArgumentException("Unknown, unsupported and degraded capabilities require a truthful user-facing reason.", nameof(disabledReason));
        }
        DisabledReason = disabledReason is null
            ? null
            : ProviderIdentifierRules.NormalizeDisplayName(disabledReason, nameof(disabledReason), maximumLength: 256);
    }

    public string CapabilityId { get; }
    public ProviderCapabilityState State { get; }
    public ProviderLifecycleState LifecycleState { get; }
    public string? DisabledReason { get; }
    public bool IsUsable => State == ProviderCapabilityState.Supported && LifecycleState != ProviderLifecycleState.Retired;
}

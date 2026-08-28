namespace CloudScribe.Providers.Abstractions;

public sealed class ProviderCapabilitySnapshot
{
    private readonly Dictionary<string, ProviderCapability> _capabilities;

    public ProviderCapabilitySnapshot(
        ProviderAccountReference account,
        DateTimeOffset capturedAtUtc,
        string provenanceId,
        IEnumerable<ProviderCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capability snapshot timestamps must be UTC.", nameof(capturedAtUtc));
        }
        Account = account;
        CapturedAtUtc = capturedAtUtc;
        ProvenanceId = ProviderIdentifierRules.NormalizeDisplayName(provenanceId, nameof(provenanceId), maximumLength: 160);
        Dictionary<string, ProviderCapability> map = new(StringComparer.Ordinal);
        foreach (ProviderCapability capability in capabilities)
        {
            if (capability is null)
            {
                throw new InvalidOperationException("Capability snapshots cannot contain null entries.");
            }
            if (!map.TryAdd(capability.CapabilityId, capability))
            {
                throw new InvalidOperationException($"Duplicate provider capability: {capability.CapabilityId}");
            }
        }
        _capabilities = map;
    }

    public ProviderAccountReference Account { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public string ProvenanceId { get; }
    public IReadOnlyCollection<ProviderCapability> Capabilities => _capabilities.Values;

    public ProviderCapability GetCapability(string capabilityId)
    {
        string normalized = ProviderIdentifierRules.NormalizeStableId(capabilityId, nameof(capabilityId), maximumLength: 96);
        return _capabilities.TryGetValue(normalized, out ProviderCapability? capability)
            ? capability
            : new ProviderCapability(
                normalized,
                ProviderCapabilityState.Unknown,
                ProviderLifecycleState.Unknown,
                "Capability is absent from the admitted provider/account snapshot.");
    }
}

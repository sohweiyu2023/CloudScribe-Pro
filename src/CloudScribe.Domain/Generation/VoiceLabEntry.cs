namespace CloudScribe.Domain.Generation;

public sealed record VoiceLabEntry(
    string ProviderStableId,
    string AccountId,
    string VoiceStableId,
    string DisplayName,
    string Language,
    IReadOnlySet<string> Capabilities,
    string PricingProvenanceId,
    string CapabilityProvenanceId,
    DateTimeOffset CapabilityObservedAtUtc,
    DateTimeOffset CapabilityExpiresAtUtc,
    bool AuditionSupported)
{
    public VoiceLabEntry Validate(DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Language);
        ArgumentNullException.ThrowIfNull(Capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityProvenanceId);
        if (CapabilityObservedAtUtc > nowUtc)
            throw new InvalidOperationException("Voice capability provenance cannot be observed in the future.");
        if (CapabilityExpiresAtUtc <= CapabilityObservedAtUtc)
            throw new InvalidOperationException("Voice capability provenance expiry must follow observation time.");
        return this;
    }

    public bool IsCapabilityStale(DateTimeOffset nowUtc) => nowUtc >= CapabilityExpiresAtUtc;

    public string StableIdentity => string.Join("/", ProviderStableId, AccountId, VoiceStableId);
}

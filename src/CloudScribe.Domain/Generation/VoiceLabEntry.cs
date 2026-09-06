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
        ValidateIdentity(
            ProviderStableId,
            AccountId,
            VoiceStableId,
            DisplayName,
            Language,
            Capabilities,
            PricingProvenanceId,
            CapabilityProvenanceId,
            CapabilityObservedAtUtc,
            CapabilityExpiresAtUtc,
            nowUtc);
        return this;
    }

    public bool IsCapabilityStale(DateTimeOffset nowUtc) => nowUtc >= CapabilityExpiresAtUtc;

    public string StableIdentity => string.Join("/", ProviderStableId, AccountId, VoiceStableId);

    private static void ValidateIdentity(
        string providerStableId,
        string accountId,
        string voiceStableId,
        string displayName,
        string language,
        IReadOnlySet<string> capabilities,
        string pricingProvenanceId,
        string capabilityProvenanceId,
        DateTimeOffset capabilityObservedAtUtc,
        DateTimeOffset capabilityExpiresAtUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityProvenanceId);
        if (capabilityObservedAtUtc > nowUtc)
            throw new InvalidOperationException("Voice capability provenance cannot be observed in the future.");
        if (capabilityExpiresAtUtc <= capabilityObservedAtUtc)
            throw new InvalidOperationException("Voice capability provenance expiry must follow observation time.");
    }
}

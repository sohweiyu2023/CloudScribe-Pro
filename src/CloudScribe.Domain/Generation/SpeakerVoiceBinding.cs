namespace CloudScribe.Domain.Generation;

public sealed record SpeakerVoiceBinding(
    string SpeakerRole,
    string ProviderStableId,
    string AccountId,
    string VoiceStableId,
    string PricingProvenanceId,
    string CapabilityProvenanceId)
{
    public SpeakerVoiceBinding Validate() => ValidateCore(
        SpeakerRole,
        ProviderStableId,
        AccountId,
        VoiceStableId,
        PricingProvenanceId,
        CapabilityProvenanceId);

    private SpeakerVoiceBinding ValidateCore(
        string speakerRole,
        string providerStableId,
        string accountId,
        string voiceStableId,
        string pricingProvenanceId,
        string capabilityProvenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityProvenanceId);
        return this;
    }

    public string RouteIdentity => string.Join("/", ProviderStableId, AccountId, VoiceStableId);
}

namespace CloudScribe.Domain.Generation;

public sealed record VoiceLabCatalogSelection(
    string VoiceStableId,
    string ProviderStableId,
    string AccountStableId,
    string ProjectStableId,
    string CapabilityEvidenceId,
    string VoiceFingerprint,
    bool CapabilityCurrent,
    bool VoiceEnabled,
    bool AccountProjectAuthorized)
{
    public VoiceLabCatalogSelection Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityEvidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceFingerprint);
        if (!CapabilityCurrent) throw new InvalidOperationException("Voice Lab selection requires current capability evidence.");
        if (!VoiceEnabled) throw new InvalidOperationException("Voice Lab selection references a disabled voice.");
        if (!AccountProjectAuthorized) throw new InvalidOperationException("Voice Lab selection is not authorized for the current account/project boundary.");
        return this;
    }
}

public static class VoiceLabCatalogSelectionPolicy
{
    public static VoiceLabCatalogSelection RequireEligible(VoiceLabCatalogSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Validate();
    }
}

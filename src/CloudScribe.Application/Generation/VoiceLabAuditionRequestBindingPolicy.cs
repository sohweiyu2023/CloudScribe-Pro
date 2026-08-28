using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public static class VoiceLabAuditionRequestBindingPolicy
{
    public static VoiceLabCatalogSelection RequireBoundSelection(
        VoiceLabCatalogSelection selection,
        string providerStableId,
        string accountStableId,
        string projectStableId,
        string voiceStableId,
        string voiceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceFingerprint);

        if (!string.Equals(selection.ProviderStableId, providerStableId, StringComparison.Ordinal) ||
            !string.Equals(selection.AccountStableId, accountStableId, StringComparison.Ordinal) ||
            !string.Equals(selection.ProjectStableId, projectStableId, StringComparison.Ordinal) ||
            !string.Equals(selection.VoiceStableId, voiceStableId, StringComparison.Ordinal) ||
            !string.Equals(selection.VoiceFingerprint, voiceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab audition request identity does not match the trusted catalog selection.");

        return selection;
    }
}

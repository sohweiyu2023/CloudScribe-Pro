namespace CloudScribe.Domain.Generation;

public sealed record VoiceCacheIsolationScope(
    string ProviderStableId,
    string AccountId,
    string ProjectId,
    string VoiceStableId,
    string VoiceFingerprint,
    bool IsPrivateOrCustomVoice)
{
    public VoiceCacheIsolationScope Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderStableId) ||
            string.IsNullOrWhiteSpace(AccountId) ||
            string.IsNullOrWhiteSpace(ProjectId) ||
            string.IsNullOrWhiteSpace(VoiceStableId) ||
            string.IsNullOrWhiteSpace(VoiceFingerprint))
        {
            throw new InvalidOperationException("Voice cache isolation identity must be complete before cache reuse is evaluated.");
        }

        return this;
    }

    public bool CanReuseWith(
        VoiceCacheIsolationScope other,
        bool explicitCurrentCrossAccountEquivalence = false)
    {
        ArgumentNullException.ThrowIfNull(other);
        Validate();
        other.Validate();

        if (!string.Equals(ProviderStableId, other.ProviderStableId, StringComparison.Ordinal) ||
            !string.Equals(VoiceStableId, other.VoiceStableId, StringComparison.Ordinal) ||
            !string.Equals(VoiceFingerprint, other.VoiceFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var sameAccount = string.Equals(AccountId, other.AccountId, StringComparison.Ordinal);
        var sameProject = string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal);
        if (sameAccount && sameProject)
        {
            return true;
        }

        if (IsPrivateOrCustomVoice || other.IsPrivateOrCustomVoice)
        {
            return false;
        }

        return explicitCurrentCrossAccountEquivalence;
    }
}

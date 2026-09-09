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
    bool AccountProjectAuthorized,
    IReadOnlyList<string>? LanguageCodes = null)
{
    public VoiceLabCatalogSelection Validate()
    {
        RequireCanonical(VoiceStableId, nameof(VoiceStableId));
        RequireCanonical(ProviderStableId, nameof(ProviderStableId));
        RequireCanonical(AccountStableId, nameof(AccountStableId));
        RequireCanonical(ProjectStableId, nameof(ProjectStableId));
        RequireCanonical(CapabilityEvidenceId, nameof(CapabilityEvidenceId));
        RequireCanonical(VoiceFingerprint, nameof(VoiceFingerprint));
        if (!CapabilityCurrent) throw new InvalidOperationException("Voice Lab selection requires current capability evidence.");
        if (!VoiceEnabled) throw new InvalidOperationException("Voice Lab selection references a disabled voice.");
        if (!AccountProjectAuthorized) throw new InvalidOperationException("Voice Lab selection is not authorized for the current account/project boundary.");
        if (LanguageCodes is not null)
        {
            if (LanguageCodes.Count == 0)
                throw new InvalidOperationException("Voice Lab selection language metadata cannot be an empty collection.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string languageCode in LanguageCodes)
            {
                RequireCanonical(languageCode, nameof(LanguageCodes));
                if (!seen.Add(languageCode))
                    throw new InvalidOperationException("Voice Lab selection language metadata contains duplicate language codes.");
            }
        }
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Voice Lab trust identity '{parameterName}' must be canonical and contain no leading or trailing whitespace.");
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new InvalidOperationException($"Voice Lab trust identity '{parameterName}' contains forbidden control characters.");
    }
}

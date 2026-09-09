namespace CloudScribe.Providers.Abstractions;

public sealed record VoiceLabProviderCatalogVoice(
    string VoiceStableId,
    string VoiceFingerprint,
    bool VoiceEnabled,
    bool AccountProjectAuthorized,
    IReadOnlyList<string>? LanguageCodes = null)
{
    public VoiceLabProviderCatalogVoice Validate()
    {
        RequireCanonical(VoiceStableId, nameof(VoiceStableId));
        RequireCanonical(VoiceFingerprint, nameof(VoiceFingerprint));
        if (LanguageCodes is not null)
        {
            if (LanguageCodes.Count == 0)
                throw new InvalidOperationException("Voice Lab provider voice language metadata cannot be an empty collection.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string languageCode in LanguageCodes)
            {
                RequireCanonical(languageCode, nameof(LanguageCodes));
                if (!seen.Add(languageCode))
                    throw new InvalidOperationException("Voice Lab provider voice language metadata contains duplicate language codes.");
            }
        }
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Voice Lab provider voice identity '{parameterName}' must be canonical.");
        }
    }
}

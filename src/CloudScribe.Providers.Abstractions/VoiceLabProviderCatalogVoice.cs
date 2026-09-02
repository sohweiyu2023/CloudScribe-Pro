namespace CloudScribe.Providers.Abstractions;

public sealed record VoiceLabProviderCatalogVoice(
    string VoiceStableId,
    string VoiceFingerprint,
    bool VoiceEnabled,
    bool AccountProjectAuthorized)
{
    public VoiceLabProviderCatalogVoice Validate()
    {
        RequireCanonical(VoiceStableId, nameof(VoiceStableId));
        RequireCanonical(VoiceFingerprint, nameof(VoiceFingerprint));
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

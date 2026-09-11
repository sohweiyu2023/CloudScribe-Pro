namespace CloudScribe.App.Composition;

/// <summary>
/// Classifies a provider-returned Google voice only from Google's documented voice-name model
/// component. This produces an identity used to select an authenticated catalog row; it does not
/// contain or invent any provider price.
/// </summary>
internal static class GoogleTtsVoiceBillingFamilyClassifier
{
    public const string Standard = "standard";
    public const string WaveNet = "wavenet";
    public const string Neural2 = "neural2";
    public const string Studio = "studio";
    public const string Chirp3Hd = "chirp3-hd";
    public const string Polyglot = "polyglot";

    public static string Classify(string voiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        string value = voiceName.Trim();
        if (!string.Equals(value, voiceName, StringComparison.Ordinal)
            || value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException("Google TTS voice identity is not canonical.");
        }

        // Google documents voice names as <locale>-<model>-<voice>. We match only explicit model
        // components and deliberately reject unknown/new families instead of assigning a cheaper
        // existing family. Matching is ordinal-ignore-case because provider examples vary in case.
        if (ContainsComponent(value, "Chirp3-HD")) return Chirp3Hd;
        if (ContainsComponent(value, "Neural2")) return Neural2;
        if (ContainsComponent(value, "WaveNet")) return WaveNet;
        if (ContainsComponent(value, "Studio")) return Studio;
        if (ContainsComponent(value, "Standard")) return Standard;
        if (ContainsComponent(value, "Polyglot")) return Polyglot;

        throw new InvalidOperationException(
            $"The selected Google voice '{value}' belongs to an unrecognized billing family. Refresh pricing/capability controls before billable generation.");
    }

    private static bool ContainsComponent(string voiceName, string modelComponent) =>
        voiceName.Contains($"-{modelComponent}-", StringComparison.OrdinalIgnoreCase);
}

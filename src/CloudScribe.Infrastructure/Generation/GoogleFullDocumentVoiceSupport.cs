namespace CloudScribe.Infrastructure.Generation;

/// <summary>Offline format support only. Current catalog, exact family pricing and independent admission remain required.</summary>
public static class GoogleFullDocumentVoiceSupport
{
    public static bool Supports(string language, string voice) => IsChirp(language, voice) || IsNeural2(language, voice);

    // Keep the historical Chirp predicate unchanged: existing plans must recompile identically.
    internal static bool IsChirp(string language, string voice) => !string.IsNullOrWhiteSpace(language)
        && !string.IsNullOrWhiteSpace(voice) && voice.Contains("-Chirp3-HD-", StringComparison.Ordinal)
        && voice.StartsWith(language + "-", StringComparison.Ordinal);

    internal static bool IsNeural2(string language, string voice)
    {
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(voice)) return false;
        string prefix = language + "-Neural2-";
        return voice.StartsWith(prefix, StringComparison.Ordinal) && voice.Length == prefix.Length + 1
            && voice[^1] is >= 'A' and <= 'Z';
    }

    internal static void RequireChirpRecovery(string language, string voice)
    {
        if (!IsChirp(language, voice))
            throw new InvalidOperationException("Chirp sentence grouping, refinement and sentence-marked recovery do not apply to this voice. Keep the admitted plain-text plan unchanged.");
    }
}

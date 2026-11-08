using CloudScribe.Domain.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Catalog selection is independent of sample text and never grants generation authority.</summary>
public sealed record GoogleVoiceComparisonSelection(VoiceLabCatalogSelection Voice, string LanguageCode)
{
    public bool MatchesReviewedVoice(string voiceName) => string.Equals(Voice.VoiceStableId, voiceName, StringComparison.Ordinal);

    public static GoogleVoiceComparisonSelection? TryCreate(VoiceLabCatalogSelection? voice, string? requestedLanguage)
    {
        if (voice is null || !string.Equals(voice.ProviderStableId, "google-cloud", StringComparison.Ordinal)) return null;
        try { voice.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
        if (voice.LanguageCodes is not { Count: > 0 } languages) return null;
        string language = languages.FirstOrDefault(code => string.Equals(code, requestedLanguage?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? languages[0];
        return new(voice, language);
    }
}

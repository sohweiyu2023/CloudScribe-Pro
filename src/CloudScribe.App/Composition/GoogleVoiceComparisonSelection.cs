using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Catalog selection is independent of sample text and never grants generation authority.</summary>
public sealed record GoogleVoiceComparisonSelection(VoiceLabCatalogSelection Voice, string LanguageCode)
{
    public void ApplyToStudio(ShellViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        // Changing locale intentionally clears stale selection, so apply current evidence last.
        model.VoiceLabLocaleFilter = LanguageCode;
        model.VoiceLabSearchText = null;
        model.SelectedVoiceLabVoice = Voice;
    }

    public bool MatchesReviewedVoice(string voiceName) => string.Equals(Voice.VoiceStableId, voiceName, StringComparison.Ordinal);

    public static GoogleVoiceComparisonSelection? TryCreate(VoiceLabCatalogSelection? voice, string? requestedLanguage)
    {
        if (voice is null || !string.Equals(voice.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)) return null;
        try { voice.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
        if (voice.LanguageCodes is not { Count: > 0 } languages) return null;
        string language = languages.FirstOrDefault(code => string.Equals(code, requestedLanguage?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? languages[0];
        return new(voice, language);
    }
}

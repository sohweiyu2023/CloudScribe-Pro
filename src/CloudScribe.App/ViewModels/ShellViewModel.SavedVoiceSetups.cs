using System.ComponentModel;
using CloudScribe.App.Composition;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private StudioPreferencesStore? _studioPreferences;
    public string? PreferredVoiceName { get; private set; }

    public void ConfigureSavedVoiceSetups(StudioPreferencesStore preferences)
    {
        _studioPreferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        SelectedThemeOption = ThemeOptions.Single(t => t.Preference == preferences.State.Theme);
        ReducedMotionEnabled = preferences.State.ReducedMotion;
        if (preferences.State.LastUsed is { } last) ApplySavedVoiceSetup(last);
        PropertyChanged += PersistDisplayPreferences;
        PropertyChanged += PersistDocumentPreference;
    }

    private void PersistDocumentPreference(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(CurrentDocumentId), StringComparison.Ordinal)
            || _studioPreferences is null || CurrentDocumentId is not Guid id) return;
        try { _studioPreferences.RememberDocument(id); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { StatusMessage = "The document is available, but its reopen preference could not be saved. Choose it from Library next time."; }
    }

    private void PersistDisplayPreferences(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SelectedThemeOption) or nameof(ReducedMotionEnabled)) || _studioPreferences is null) return;
        try { _studioPreferences.RememberDisplay(SelectedThemeOption?.Preference ?? Design.ThemePreference.FollowSystem, ReducedMotionEnabled); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { StatusMessage = "Display preference applies for this session, but could not be saved. Existing preferences were preserved."; }
    }

    public void ApplySavedVoiceSetup(VoiceSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        VoiceLabLocaleFilter = setup.Locale;
        VoiceLabSearchText = null;
        PreferredVoiceName = setup.VoiceName;
        // Remember intent, never adopt cached authorization or select a provider implicitly.
        SelectedVoiceLabVoice = null;
        OnPropertyChanged(nameof(PreferredVoiceName));
        StatusMessage = "Saved voice setup loaded. Refresh real Google voices to validate it before use.";
    }

    public void RememberSelectedVoiceSetup()
    {
        if (_studioPreferences is null || SelectedVoiceLabVoice is not { } voice) return;
        string? locale = VoiceLabLocaleFilter ?? (voice.LanguageCodes is { Count: > 0 } codes ? codes[0] : null);
        if (string.IsNullOrWhiteSpace(locale)) return;
        try
        {
            _studioPreferences.Remember(new VoiceSetup("Last used", locale, voice.VoiceStableId));
            PreferredVoiceName = voice.VoiceStableId;
            OnPropertyChanged(nameof(PreferredVoiceName));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = "Voice selected for this session, but its reusable setup could not be saved. Existing preferences were preserved.";
        }
    }

    private void RestorePreferredCatalogVoice()
    {
        if (SelectedVoiceLabVoice is not null) return;
        if (PreferredVoiceName is not null)
            SelectedVoiceLabVoice = VoiceLabCatalogResults.FirstOrDefault(v => string.Equals(v.VoiceStableId, PreferredVoiceName, StringComparison.Ordinal));
        else if (VoiceLabCatalogResults.Count == 1)
            SelectedVoiceLabVoice = VoiceLabCatalogResults[0];
    }
}

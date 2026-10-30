namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IReadOnlyList<string> _voiceLanguageCodes = Array.Empty<string>();
    private bool _hasCurrentVoiceLanguageCatalog;
    private DateTimeOffset _voiceLanguageCatalogExpires;
    private string? _voiceLanguageCatalogScope;

    public IReadOnlyList<string> VoiceLanguageCodes => _voiceLanguageCodes;
    public bool HasCurrentVoiceLanguageCatalog => _hasCurrentVoiceLanguageCatalog && _timeProvider.GetUtcNow() < _voiceLanguageCatalogExpires;
    public string? VoiceLanguageCatalogScope => HasCurrentVoiceLanguageCatalog ? _voiceLanguageCatalogScope : null;

    public void SetVoiceLanguageCatalog(IEnumerable<string> codes, DateTimeOffset expiresAtUtc, string scope)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _voiceLanguageCodes = codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToArray();
        _voiceLanguageCatalogExpires = expiresAtUtc;
        _voiceLanguageCatalogScope = scope;
        _hasCurrentVoiceLanguageCatalog = _timeProvider.GetUtcNow() < expiresAtUtc;
        OnPropertyChanged(nameof(VoiceLanguageCodes));
        OnPropertyChanged(nameof(HasCurrentVoiceLanguageCatalog));
        OnPropertyChanged(nameof(VoiceLanguageCatalogScope));
        if (!_hasCurrentVoiceLanguageCatalog) ExpireVoiceLanguageCatalog();
    }

    public void InvalidateVoiceLanguageCatalog()
    {
        _hasCurrentVoiceLanguageCatalog = false;
        _voiceLanguageCatalogScope = null;
        OnPropertyChanged(nameof(HasCurrentVoiceLanguageCatalog));
        OnPropertyChanged(nameof(VoiceLanguageCatalogScope));
    }

    public void RefreshVoiceLanguageCatalogFreshness()
    {
        if (!_hasCurrentVoiceLanguageCatalog || HasCurrentVoiceLanguageCatalog) return;
        ExpireVoiceLanguageCatalog();
    }

    private void ExpireVoiceLanguageCatalog()
    {
        InvalidateVoiceLanguageCatalog();
        SelectedVoiceLabVoice = null;
        VoiceLabCatalogResults.Clear();
        VoiceLabCatalogLastRefreshSucceeded = false;
        ConnectedVoiceCatalogStatus = "The last catalog verification expired. Refresh voices before selecting a voice or reviewing cost. Saved preferences are retained.";
    }
}

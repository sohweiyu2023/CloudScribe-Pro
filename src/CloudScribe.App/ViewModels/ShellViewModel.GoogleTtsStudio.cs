namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private string? _voiceLabLocaleFilter;
    private string? _voiceLabSearchText;

    /// <summary>
    /// Optional user-entered BCP-47 locale filter used only to narrow the authenticated
    /// Voice Lab catalog query. It is request intent, never authorization evidence.
    /// </summary>
    public string? VoiceLabLocaleFilter
    {
        get => _voiceLabLocaleFilter;
        set => SetProperty(ref _voiceLabLocaleFilter, NormalizeOptional(value));
    }

    /// <summary>
    /// Optional user-entered voice search text used only to narrow the authenticated catalog.
    /// </summary>
    public string? VoiceLabSearchText
    {
        get => _voiceLabSearchText;
        set => SetProperty(ref _voiceLabSearchText, NormalizeOptional(value));
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim();
        if (normalized.Contains('\r') || normalized.Contains('\n') || normalized.Contains('\0'))
            throw new InvalidOperationException("Google TTS catalog filters cannot contain control characters.");
        return normalized;
    }
}

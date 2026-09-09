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

    /// <summary>
    /// Captures only what the user can currently see/select in Studio: the open local document,
    /// its exact current text, and the selected authenticated catalog voice. This is deliberately
    /// request intent, not provider authorization or trust evidence. Stage6 must independently
    /// resolve those production facts after this capture.
    /// </summary>
    public GoogleTtsStudioRequestSelection CaptureGoogleTtsStudioRequestSelection()
    {
        Guid documentId = CurrentDocumentId
            ?? throw new InvalidOperationException("Open a local document before preparing Google generation.");
        string text = DocumentText;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The current local document has no text to synthesize.");

        VoiceLabCatalogSelection voice = SelectedVoiceLabVoice
            ?? throw new InvalidOperationException("Select a verified Google voice before preparing generation.");
        voice.Validate();

        string voiceStableId = RequireCanonical(voice.VoiceStableId, "selected Google voice identity");
        string? locale = NormalizeOptional(VoiceLabLocaleFilter);
        return new GoogleTtsStudioRequestSelection(
            documentId,
            CurrentRevisionId,
            text,
            voiceStableId,
            locale,
            DateTimeOffset.UtcNow);
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

    private static string RequireCanonical(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Google TTS {label} is unavailable.");
        string normalized = value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal)
            || normalized.Contains('\r')
            || normalized.Contains('\n')
            || normalized.Contains('\0'))
        {
            throw new InvalidOperationException($"Google TTS {label} is not canonical.");
        }
        return normalized;
    }

    public sealed record GoogleTtsStudioRequestSelection(
        Guid DocumentId,
        Guid? RevisionId,
        string ExactText,
        string VoiceStableId,
        string? Locale,
        DateTimeOffset CapturedAtUtc);
}

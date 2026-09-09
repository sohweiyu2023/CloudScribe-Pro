using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

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
    /// Captures only what the user can currently see/select in Studio plus the canonical identities
    /// attached to that authenticated catalog selection: open local document, exact current text,
    /// provider/account/project, selected voice, authoritative provider-returned language code,
    /// capability-evidence identity and voice fingerprint. These identifiers let Stage6 re-resolve
    /// current production evidence without guessing. The capture itself still makes no synthesis-
    /// authorization, pricing-current, trust-current, queue, spend, or reconciliation assertion.
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

        string providerStableId = RequireCanonical(voice.ProviderStableId, "selected Google provider identity");
        string accountStableId = RequireCanonical(voice.AccountStableId, "selected Google account identity");
        string projectStableId = RequireCanonical(voice.ProjectStableId, "selected Google project identity");
        string voiceStableId = RequireCanonical(voice.VoiceStableId, "selected Google voice identity");
        string capabilityEvidenceId = RequireCanonical(
            voice.CapabilityEvidenceId,
            "selected Google capability evidence identity");
        string voiceFingerprint = RequireCanonical(voice.VoiceFingerprint, "selected Google voice fingerprint");
        string? locale = NormalizeOptional(VoiceLabLocaleFilter);
        string languageCode = ResolveAuthoritativeLanguageCode(voice, locale);

        return new GoogleTtsStudioRequestSelection(
            documentId,
            CurrentRevisionId,
            text,
            providerStableId,
            accountStableId,
            projectStableId,
            voiceStableId,
            capabilityEvidenceId,
            voiceFingerprint,
            languageCode,
            locale,
            DateTimeOffset.UtcNow);
    }

    private static string ResolveAuthoritativeLanguageCode(
        VoiceLabCatalogSelection voice,
        string? requestedLocale)
    {
        IReadOnlyList<string> languageCodes = voice.LanguageCodes
            ?? throw new InvalidOperationException(
                "The selected Google voice has no provider-returned language metadata. Refresh the authenticated voice catalog.");
        if (languageCodes.Count == 0)
            throw new InvalidOperationException(
                "The selected Google voice has no provider-returned language metadata. Refresh the authenticated voice catalog.");

        string[] canonical = languageCodes
            .Select(code => RequireCanonical(code, "selected Google voice language code"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(requestedLocale))
        {
            string? exact = canonical.SingleOrDefault(code =>
                string.Equals(code, requestedLocale, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            string[] compatible = canonical
                .Where(code => LocaleMatches(code, requestedLocale))
                .ToArray();
            if (compatible.Length == 1)
                return compatible[0];
            if (compatible.Length > 1)
            {
                throw new InvalidOperationException(
                    "The selected Google voice supports multiple languages compatible with the locale filter. Select a specific locale before generation.");
            }

            throw new InvalidOperationException(
                "The selected Google voice no longer supports the requested locale. Refresh the authenticated voice catalog.");
        }

        if (canonical.Length == 1)
            return canonical[0];

        throw new InvalidOperationException(
            "The selected Google voice supports multiple languages. Select a specific locale before generation.");
    }

    private static bool LocaleMatches(string languageCode, string requestedLocale) =>
        string.Equals(languageCode, requestedLocale, StringComparison.OrdinalIgnoreCase) ||
        languageCode.StartsWith(requestedLocale + "-", StringComparison.OrdinalIgnoreCase) ||
        requestedLocale.StartsWith(languageCode + "-", StringComparison.OrdinalIgnoreCase);

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
        string ProviderStableId,
        string AccountStableId,
        string ProjectStableId,
        string VoiceStableId,
        string CapabilityEvidenceId,
        string VoiceFingerprint,
        string LanguageCode,
        string? Locale,
        DateTimeOffset CapturedAtUtc);
}

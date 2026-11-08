using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    /// <summary>The selected verified voice for a batch, independent of the open document.
    /// Returns null with a plain explanation when no usable voice is selected.</summary>
    public GoogleTtsStudioRequestSelection? TryCaptureBatchVoice(out string? problem)
    {
        problem = null;
        if (SelectedVoiceLabVoice is not { } voice)
        { problem = "Select a verified Google voice first. The batch uses one voice for every file."; return null; }
        try
        {
            voice.Validate();
            string? locale = NormalizeOptional(VoiceLabLocaleFilter);
            return new(Guid.Empty, null, string.Empty,
                RequireCanonical(voice.ProviderStableId, "selected Google provider identity"),
                RequireCanonical(voice.AccountStableId, "selected Google account identity"),
                RequireCanonical(voice.ProjectStableId, "selected Google project identity"),
                RequireCanonical(voice.VoiceStableId, "selected Google voice identity"),
                RequireCanonical(voice.CapabilityEvidenceId, "selected Google capability evidence identity"),
                RequireCanonical(voice.VoiceFingerprint, "selected Google voice fingerprint"),
                ResolveAuthoritativeLanguageCode(voice, locale), locale, DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException)
        { problem = "The selected voice is not verified for this account. Refresh voices and select it again."; return null; }
    }

    /// <summary>Voices the batch window offers: verified, enabled and authorized for the current account and project.</summary>
    public IReadOnlyList<CloudScribe.Domain.Generation.VoiceLabCatalogSelection> BatchVoiceChoices =>
        VoiceLabCatalogResults.Where(v => v.CapabilityCurrent && v.VoiceEnabled && v.AccountProjectAuthorized)
            .OrderBy(v => v.VoiceStableId, StringComparer.Ordinal).ToArray();

    /// <summary>Selects the verified voice with this identity for the batch (the same selection the main window uses).
    /// Returns false, changing nothing, when that voice is not in the current verified catalog.</summary>
    public bool TrySelectBatchVoice(string voiceStableId)
    {
        var match = BatchVoiceChoices.FirstOrDefault(v => string.Equals(v.VoiceStableId, voiceStableId, StringComparison.Ordinal));
        if (match is null) return false;
        SelectedVoiceLabVoice = match;
        return true;
    }

    /// <summary>Refreshes the verified voice catalog, when the main window could refresh it now. Sends no synthesis request.</summary>
    public async Task<bool> RefreshBatchVoicesAsync()
    {
        if (!RefreshVoiceLabCatalogCommand.CanExecute(null)) return false;
        await RefreshVoiceLabCatalogCommand.ExecuteAsync(null).ConfigureAwait(true);
        return true;
    }

    /// <summary>The same verified voice applied to one batch document.</summary>
    public static GoogleTtsStudioRequestSelection ForBatchDocument(GoogleTtsStudioRequestSelection voice, Guid documentId, Guid revisionId, string text)
    {
        ArgumentNullException.ThrowIfNull(voice);
        return voice with { DocumentId = documentId, RevisionId = revisionId, ExactText = text, CapturedAtUtc = DateTimeOffset.UtcNow };
    }

    public static BatchSettings BatchSettingsFor(GoogleTtsStudioRequestSelection voice, BatchFailurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(voice);
        string label = voice.VoiceStableId.Split('-').LastOrDefault() is { Length: > 0 } name ? $"{name} ({voice.LanguageCode})" : voice.VoiceStableId;
        return new(voice.LanguageCode, voice.VoiceStableId, label, BatchEngines.CompleteArticle, policy);
    }

    /// <summary>Holds the Google generation operation for a whole batch, so no other generation can start meanwhile.</summary>
    public async Task RunBatchOperationAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnterGoogleGenerationOperation();
        try
        {
            InvalidatePrivateGoogleDiagnostic(); // Batches never arm sensitive capture.
            PreparedGoogleGenerationSpendApproved = false;
            PreparedGoogleGenerationSpendReview = null;
            await operation(cancellationToken).ConfigureAwait(true);
        }
        finally { ExitGoogleGenerationOperation(); }
    }

    public void RefreshDocumentLibraryAfterBatchImport()
    {
        if (RefreshDocumentLibraryCommand.CanExecute(null)) RefreshDocumentLibraryCommand.Execute(null);
    }
}

using CloudScribe.App.ViewModels;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App.Composition;

public sealed class GoogleConnectedVoiceCatalogLoader(
    GoogleTextToSpeechCatalogBootstrapService bootstrapService,
    IProviderAccountStore accounts, ILogger? catalogLogger = null)
{
    private readonly GoogleTextToSpeechCatalogBootstrapService _bootstrapService = bootstrapService;
    public Task<bool> RefreshAsync(ShellViewModel model, CancellationToken token) => LoadAsync(model, false, token);

    public Task<bool> ApplyFiltersAsync(ShellViewModel model, CancellationToken token) => LoadAsync(model, true, token);

    private async Task<bool> LoadAsync(ShellViewModel model, bool retainCurrentEvidence, CancellationToken token)
    {
        if (model.IsConnectedVoiceCatalogLoading) return false;
        model.IsConnectedVoiceCatalogLoading = true;
        model.InvalidateVoiceLanguageCatalog();
        bool restrictedRefresh = true; // Unknown gate state must not suggest an ordinary retry.
        VoiceLabCatalogSelection? previous = model.SelectedVoiceLabVoice;
        model.SelectedVoiceLabVoice = null;
        model.VoiceLabCatalogResults.Clear();
        Guid refreshId = Guid.NewGuid();
        GoogleCatalogRefreshPhase phase = GoogleCatalogRefreshPhase.UiBootstrap;
        try
        {
            restrictedRefresh = _bootstrapService.RequiresReviewedRefresh;
            if (retainCurrentEvidence && restrictedRefresh)
            {
                model.ConnectedVoiceCatalogStatus = DiagnosticOpeningMessage;
                return false;
            }
            model.ConnectedVoiceCatalogStatus = restrictedRefresh
                ? "Checking voices within the diagnostic run. Closing or timing out can stop this run and require review. Existing audio and reservations are retained."
                : "Loading voices from your saved Google connection. This does not generate speech. You can close this window to stop waiting.";
            using var completion = retainCurrentEvidence ? null : _bootstrapService.BeginUiRefresh(refreshId);
            GoogleCatalogRefreshDiagnostics.Phase(catalogLogger, refreshId, phase);
            var observed = retainCurrentEvidence
                ? await _bootstrapService.LoadConfiguredForOpeningAsync(refreshId, token).ConfigureAwait(true)
                : await _bootstrapService.RefreshConfiguredAsync(refreshId, token).ConfigureAwait(true);
            phase = GoogleCatalogRefreshPhase.UiQuery;
            GoogleCatalogRefreshDiagnostics.Phase(catalogLogger, refreshId, phase);
            await model.RefreshVoiceCatalogForUiAsync(refreshId, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!model.VoiceLabCatalogLastRefreshSucceeded)
            {
                return VoiceCatalogRefreshPresentation.RejectIncomplete(model, restrictedRefresh, model.StatusMessage);
            }
            if (!PublishObservedCatalog(model, observed, previous, restrictedRefresh)) return false;
            completion?.Succeed();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            model.SelectedVoiceLabVoice = null;
            model.VoiceLabCatalogResults.Clear();
            GoogleCatalogRefreshDiagnostics.Failure(catalogLogger, refreshId, phase, exception);
            model.ConnectedVoiceCatalogStatus = VoiceCatalogErrorMessages.For(exception, phase, restrictedRefresh);
            model.StatusMessage = model.ConnectedVoiceCatalogStatus;
            return false;
        }
        finally { model.IsConnectedVoiceCatalogLoading = false; }
    }

    private static bool PublishObservedCatalog(ShellViewModel model, GoogleTextToSpeechCatalogBootstrapResult observed,
        VoiceLabCatalogSelection? previous, bool restrictedRefresh)
    {
        RequireCoherentSelections(model, observed);
        model.SelectedVoiceLabVoice = model.VoiceLabCatalogResults.FirstOrDefault(candidate => previous is not null
            && string.Equals(candidate.VoiceStableId, previous.VoiceStableId, StringComparison.Ordinal)
            && string.Equals(candidate.VoiceFingerprint, previous.VoiceFingerprint, StringComparison.Ordinal)
            && string.Equals(candidate.CapabilityEvidenceId, previous.CapabilityEvidenceId, StringComparison.Ordinal)
            && string.Equals(candidate.AccountStableId, previous.AccountStableId, StringComparison.Ordinal)
            && string.Equals(candidate.ProjectStableId, previous.ProjectStableId, StringComparison.Ordinal)) ?? model.SelectedVoiceLabVoice;
        model.SetVoiceLanguageCatalog(observed.Voices.SelectMany(voice => voice.LanguageCodes), observed.ExpiresAtUtc, observed.CatalogProvenanceId);
        if (!model.HasCurrentVoiceLanguageCatalog)
        {
            return VoiceCatalogRefreshPresentation.RejectIncomplete(model, restrictedRefresh, model.ConnectedVoiceCatalogStatus);
        }
        model.ConnectedVoiceCatalogStatus = model.VoiceLabCatalogResults.Count == 0
            ? "No verified voices match the selected language and search. Choose a language from the list or clear the voice search, then apply voice filters."
            : $"{model.VoiceLabCatalogResults.Count} verified voices loaded. " + (observed.RetainedCurrentEvidence
                ? "Current evidence and its original expiry were retained. " : "New verification evidence was saved. ") + "Choose a voice below. No speech was generated.";
        return true;
    }

    private const string DiagnosticOpeningMessage = "Diagnostic mode is active. Opening this window or applying filters has not started verification or consumed a refresh. Review the diagnostic state before explicitly refreshing; interrupted verification can stop the run. Existing audio and reservations are retained.";

    private static void RequireCoherentSelections(ShellViewModel model, GoogleTextToSpeechCatalogBootstrapResult observed)
    {
        foreach (var selection in model.VoiceLabCatalogResults)
        {
            var voice = observed.Voices.SingleOrDefault(candidate => string.Equals(candidate.Name, selection.VoiceStableId, StringComparison.Ordinal));
            if (voice is null || !string.Equals(selection.CapabilityEvidenceId, observed.CapabilityEvidence.Id.ToString("D"), StringComparison.Ordinal)
                || !string.Equals(selection.ProviderStableId, observed.Account.Reference.ProviderStableId, StringComparison.Ordinal)
                || !string.Equals(selection.AccountStableId, observed.Account.Reference.AccountId, StringComparison.Ordinal)
                || !string.Equals(selection.ProjectStableId, observed.ProjectId, StringComparison.Ordinal)
                || !string.Equals(selection.VoiceFingerprint, GoogleVoiceCatalogIdentity.CreateFingerprint(observed.CatalogProvenanceId, voice), StringComparison.Ordinal))
                throw new InvalidOperationException("The voice catalog changed while loading. Verify the connection again before reviewing cost.");
        }
    }

    public async Task OpenAsync(ShellViewModel model, CancellationToken token)
    {
        try
        {
            int count = (await accounts.ListAsync(token).ConfigureAwait(true)).Count(account => account.IsEnabled &&
                string.Equals(account.Reference.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal));
            if (count == 1 && _bootstrapService.RequiresReviewedRefresh)
            {
                model.ConnectedVoiceCatalogStatus = DiagnosticOpeningMessage;
                return;
            }
            if (count == 1) await ApplyFiltersAsync(model, token).ConfigureAwait(true);
            else model.ConnectedVoiceCatalogStatus = count == 0
                ? "Connect Google once before voices can load. Use Help and Include Google setup guide; your document stays local."
                : "Several Google connections are enabled. Inspect the connections in Setups before refreshing; no account was selected automatically.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            GoogleCatalogRefreshDiagnostics.Failure(catalogLogger, Guid.NewGuid(), GoogleCatalogRefreshPhase.UiAccountLookup, exception);
            model.ConnectedVoiceCatalogStatus = VoiceCatalogErrorMessages.For(exception, GoogleCatalogRefreshPhase.UiAccountLookup);
        }
    }
}

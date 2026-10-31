using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private async Task<bool> RefreshConnectedVoicesAsync(ShellViewModel model, CancellationToken token)
    {
        if (model.IsConnectedVoiceCatalogLoading) return false;
        model.IsConnectedVoiceCatalogLoading = true;
        model.InvalidateVoiceLanguageCatalog();
        model.ConnectedVoiceCatalogStatus = "Loading voices from your saved Google connection. This does not generate speech. You can close this window to stop waiting.";
        model.SelectedVoiceLabVoice = null;
        model.VoiceLabCatalogResults.Clear();
        Guid refreshId = Guid.NewGuid();
        GoogleCatalogRefreshPhase phase = GoogleCatalogRefreshPhase.UiBootstrap;
        try
        {
            using var completion = _bootstrapService.BeginUiRefresh(refreshId);
            GoogleCatalogRefreshDiagnostics.Phase(catalogLogger, refreshId, phase);
            var observed = await _bootstrapService.RefreshConfiguredAsync(refreshId, token).ConfigureAwait(true);
            phase = GoogleCatalogRefreshPhase.UiQuery;
            GoogleCatalogRefreshDiagnostics.Phase(catalogLogger, refreshId, phase);
            await model.RefreshVoiceCatalogForUiAsync(refreshId, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!model.VoiceLabCatalogLastRefreshSucceeded)
            {
                model.ConnectedVoiceCatalogStatus = model.StatusMessage;
                return false;
            }
            model.SetVoiceLanguageCatalog(observed.Voices.SelectMany(voice => voice.LanguageCodes), observed.ExpiresAtUtc, observed.CatalogProvenanceId);
            if (!model.HasCurrentVoiceLanguageCatalog)
            {
                model.StatusMessage = model.ConnectedVoiceCatalogStatus;
                return false;
            }
            model.ConnectedVoiceCatalogStatus = model.VoiceLabCatalogResults.Count == 0
                ? "No verified voices match the selected language and search. Choose a language from the list or clear the voice search, then refresh."
                : $"{model.VoiceLabCatalogResults.Count} verified voices loaded. Choose a voice below. No speech was generated.";
            completion.Succeed();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            model.SelectedVoiceLabVoice = null;
            model.VoiceLabCatalogResults.Clear();
            GoogleCatalogRefreshDiagnostics.Failure(catalogLogger, refreshId, phase, exception);
            model.ConnectedVoiceCatalogStatus = VoiceCatalogErrorMessages.For(exception, phase);
            model.StatusMessage = model.ConnectedVoiceCatalogStatus;
            return false;
        }
        finally { model.IsConnectedVoiceCatalogLoading = false; }
    }

    private async Task AutoLoadConnectedVoicesAsync(ShellViewModel model, Func<Task> refresh, CancellationToken token)
    {
        try
        {
            int count = (await accounts.ListAsync(token).ConfigureAwait(true)).Count(account => account.IsEnabled &&
                string.Equals(account.Reference.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal));
            if (count == 1) await refresh().ConfigureAwait(true);
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

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
        model.ConnectedVoiceCatalogStatus = "Loading voices from your saved Google connection. This does not generate speech. You can close this window to stop waiting.";
        model.SelectedVoiceLabVoice = null;
        model.VoiceLabCatalogResults.Clear();
        try
        {
            await _bootstrapService.RefreshConfiguredAsync(token).ConfigureAwait(true);
            await model.RefreshVoiceCatalogForUiAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!model.VoiceLabCatalogLastRefreshSucceeded)
                throw new InvalidOperationException("Catalog verification did not complete.");
            model.ConnectedVoiceCatalogStatus = model.VoiceLabCatalogResults.Count == 0
                ? "No voices match these filters. Check the language code (for example en-US), or use Clear voice search and reload."
                : $"{model.VoiceLabCatalogResults.Count} verified voices loaded. Choose a voice below. No speech was generated.";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            model.SelectedVoiceLabVoice = null;
            model.VoiceLabCatalogResults.Clear();
            model.ConnectedVoiceCatalogStatus = VoiceCatalogErrorMessages.For(exception);
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
        { model.ConnectedVoiceCatalogStatus = VoiceCatalogErrorMessages.For(exception); }
    }
}

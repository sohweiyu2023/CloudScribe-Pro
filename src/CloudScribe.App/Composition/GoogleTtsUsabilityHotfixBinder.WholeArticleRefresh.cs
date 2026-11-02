using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private async Task<ShellViewModel.GoogleTtsStudioRequestSelection> RefreshWholeArticleSelectionOnUiAsync(
        ShellViewModel model, ShellViewModel.GoogleTtsStudioRequestSelection original, CancellationToken token)
    {
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => RefreshWholeArticleSelectionAsync(model, original, token)).ConfigureAwait(false);
    }

    private async Task<ShellViewModel.GoogleTtsStudioRequestSelection> RefreshWholeArticleSelectionAsync(
        ShellViewModel model, ShellViewModel.GoogleTtsStudioRequestSelection original, CancellationToken token)
    {
        if (!await RefreshConnectedVoicesAsync(model, token).ConfigureAwait(true))
            throw new InvalidOperationException("The bounded catalog refresh failed. No speech request was reserved or retried.");
        var matching = model.VoiceLabCatalogResults.Where(v =>
            string.Equals(v.ProviderStableId, original.ProviderStableId, StringComparison.Ordinal)
            && string.Equals(v.AccountStableId, original.AccountStableId, StringComparison.Ordinal)
            && string.Equals(v.ProjectStableId, original.ProjectStableId, StringComparison.Ordinal)
            && string.Equals(v.VoiceStableId, original.VoiceStableId, StringComparison.Ordinal)
            && string.Equals(v.VoiceFingerprint, original.VoiceFingerprint, StringComparison.Ordinal)
            && v.LanguageCodes?.Contains(original.LanguageCode, StringComparer.Ordinal) == true).ToArray();
        if (matching.Length != 1) throw new InvalidOperationException("The exact reviewed voice is no longer uniquely verified. No fallback voice was selected.");
        model.SelectedVoiceLabVoice = matching[0];
        return model.CaptureGoogleTtsStudioRequestSelection();
    }
}

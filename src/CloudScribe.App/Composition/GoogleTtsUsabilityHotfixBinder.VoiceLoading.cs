using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private readonly GoogleConnectedVoiceCatalogLoader _connectedVoiceCatalog = new(bootstrapService, accounts, catalogLogger);

    private Task<bool> RefreshConnectedVoicesAsync(ShellViewModel model, CancellationToken token) =>
        _connectedVoiceCatalog.RefreshAsync(model, token);

    private async Task AutoLoadConnectedVoicesAsync(ShellViewModel model, CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(30));
        await _connectedVoiceCatalog.OpenAsync(model, request.Token).ConfigureAwait(true);
    }
}

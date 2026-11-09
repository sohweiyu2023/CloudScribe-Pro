namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _isConnectedVoiceCatalogLoading;
    private string _connectedVoiceCatalogStatus = "Voices have not loaded yet. Open Create MP3 to check your saved connection automatically.";

    public bool IsConnectedVoiceCatalogLoading
    {
        get => _isConnectedVoiceCatalogLoading;
        set => SetProperty(ref _isConnectedVoiceCatalogLoading, value);
    }

    public string ConnectedVoiceCatalogStatus
    {
        get => _connectedVoiceCatalogStatus;
        set => SetProperty(ref _connectedVoiceCatalogStatus, value);
    }

    public bool VoiceLabCatalogLastRefreshSucceeded { get; private set; }

    // Captured before bootstrap as well as before the UI query. A saved setup can
    // change while either await is outstanding, including another setup in the same locale.
    internal int VoiceCatalogFilterVersion => Volatile.Read(ref _voiceCatalogFilterVersion);

    internal int RetireVoiceCatalogPresentation() => Interlocked.Increment(ref _voiceCatalogFilterVersion);

    public Task RefreshVoiceCatalogForUiAsync(CancellationToken token) => RefreshVoiceLabCatalogAsync(token);

    public Task RefreshVoiceCatalogForUiAsync(Guid refreshId, CancellationToken token) => RefreshVoiceLabCatalogObservedAsync(refreshId, token);
}

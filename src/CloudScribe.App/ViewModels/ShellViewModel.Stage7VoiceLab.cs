using System.Collections.ObjectModel;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed record VoiceLabCatalogUiState(
    VoiceLabCatalogQuery Query,
    bool AccountAuthorized,
    bool ProjectAuthorized,
    bool PrivateVoiceAccessAuthorized);

public sealed partial class ShellViewModel
{
    private VoiceLabCatalogQueryService? _voiceLabCatalog;
    private Func<VoiceLabCatalogUiState>? _captureVoiceLabCatalogState;

    public ObservableCollection<VoiceLabCatalogSelection> VoiceLabCatalogResults { get; } = [];

    public bool CanRefreshVoiceLabCatalog =>
        _voiceLabCatalog is not null && _captureVoiceLabCatalogState is not null;

    public void ConfigureStage7VoiceLabCatalog(
        VoiceLabCatalogQueryService catalogService,
        Func<VoiceLabCatalogUiState> captureCurrentState)
    {
        _voiceLabCatalog = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _captureVoiceLabCatalogState = captureCurrentState ?? throw new ArgumentNullException(nameof(captureCurrentState));
        OnPropertyChanged(nameof(CanRefreshVoiceLabCatalog));
        RefreshVoiceLabCatalogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshVoiceLabCatalog))]
    private async Task RefreshVoiceLabCatalogAsync(CancellationToken cancellationToken)
    {
        var catalog = _voiceLabCatalog
            ?? throw new InvalidOperationException("Voice Lab catalog is not configured.");
        var capture = _captureVoiceLabCatalogState
            ?? throw new InvalidOperationException("Voice Lab catalog UI state capture is not configured.");

        cancellationToken.ThrowIfCancellationRequested();
        var state = capture() ?? throw new InvalidOperationException("Voice Lab catalog UI state is unavailable.");
        var results = await catalog.QueryAsync(
            state.Query,
            state.AccountAuthorized,
            state.ProjectAuthorized,
            state.PrivateVoiceAccessAuthorized,
            cancellationToken).ConfigureAwait(true);

        cancellationToken.ThrowIfCancellationRequested();
        VoiceLabCatalogResults.Clear();
        foreach (var selection in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VoiceLabCatalogResults.Add(selection);
        }

        StatusMessage = $"Voice Lab · {VoiceLabCatalogResults.Count} trusted voices";
    }
}

using System.Collections.ObjectModel;
using CloudScribe.App.Composition;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private VoiceLabCatalogQueryService? _voiceLabCatalog;
    private Func<CancellationToken, Task<VoiceLabCatalogUiState>>? _captureVoiceLabCatalogState;
    private Func<VoiceLabCatalogSelection, VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionExecutionService>>? _createVoiceLabAuditionService;
    private Func<VoiceLabCatalogSelection, CancellationToken, Task<VoiceLabAuditionRequest>>? _captureVoiceLabAuditionRequest;
    private Func<VoiceLabCatalogSelection, CancellationToken, Task<VoiceLabCatalogSelection>>? _refreshVoiceLabSelection;
    private VoiceLabCatalogSelection? _selectedVoiceLabVoice;
    private int _voiceLabCatalogRefreshInFlight;
    private int _voiceLabAuditionInFlight;

    public ObservableCollection<VoiceLabCatalogSelection> VoiceLabCatalogResults { get; } = [];

    public VoiceLabCatalogSelection? SelectedVoiceLabVoice
    {
        get => _selectedVoiceLabVoice;
        set
        {
            if (value is not null)
                value.Validate();
            if (!SetProperty(ref _selectedVoiceLabVoice, value))
                return;

            OnPropertyChanged(nameof(CanAuditionSelectedVoice));
            AuditionSelectedVoiceCommand.NotifyCanExecuteChanged();
            RefreshVoiceLabRouteAction();
        }
    }

    public bool CanRefreshVoiceLabCatalog =>
        _voiceLabCatalog is not null &&
        _captureVoiceLabCatalogState is not null &&
        Volatile.Read(ref _voiceLabCatalogRefreshInFlight) == 0;

    public bool CanAuditionSelectedVoice =>
        Volatile.Read(ref _googleCredentialRepairActive) == 0 &&
        SelectedVoiceLabVoice is not null &&
        _createVoiceLabAuditionService is not null &&
        _captureVoiceLabAuditionRequest is not null &&
        _refreshVoiceLabSelection is not null &&
        Volatile.Read(ref _voiceLabAuditionInFlight) == 0;

    public void ConfigureStage7VoiceLabCatalog(
        VoiceLabCatalogQueryService catalogService,
        Func<CancellationToken, Task<VoiceLabCatalogUiState>> captureCurrentState)
    {
        _voiceLabCatalog = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _captureVoiceLabCatalogState = captureCurrentState ?? throw new ArgumentNullException(nameof(captureCurrentState));
        OnPropertyChanged(nameof(CanRefreshVoiceLabCatalog));
        RefreshVoiceLabCatalogCommand.NotifyCanExecuteChanged();
        RefreshVoiceLabRouteAction();
    }

    public void ConfigureStage7VoiceLabAudition(
        Func<VoiceLabCatalogSelection, VoiceLabAuditionRequest, CancellationToken, Task<VoiceLabAuditionExecutionService>> createAuditionServiceAsync,
        Func<VoiceLabCatalogSelection, CancellationToken, Task<VoiceLabAuditionRequest>> captureCurrentRequestAsync,
        Func<VoiceLabCatalogSelection, CancellationToken, Task<VoiceLabCatalogSelection>> refreshCurrentSelectionAsync)
    {
        _createVoiceLabAuditionService = createAuditionServiceAsync ?? throw new ArgumentNullException(nameof(createAuditionServiceAsync));
        _captureVoiceLabAuditionRequest = captureCurrentRequestAsync ?? throw new ArgumentNullException(nameof(captureCurrentRequestAsync));
        _refreshVoiceLabSelection = refreshCurrentSelectionAsync ?? throw new ArgumentNullException(nameof(refreshCurrentSelectionAsync));
        OnPropertyChanged(nameof(CanAuditionSelectedVoice));
        AuditionSelectedVoiceCommand.NotifyCanExecuteChanged();
        RefreshVoiceLabRouteAction();
    }

    private void RefreshVoiceLabRouteAction()
    {
        if (!_pages.TryGetValue(AppRoute.Audio, out RoutePageViewModel? page) || page is null)
            return;

        page.HasPrimaryAction = true;
        page.PrimaryActionLabel = "Open saved MP3 folder";
        page.PrimaryActionCommand = OpenSavedAudioFolderCommand;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshVoiceLabCatalog))]
    private Task RefreshVoiceLabCatalogAsync(CancellationToken cancellationToken) =>
        RefreshVoiceLabCatalogObservedAsync(Guid.NewGuid(), cancellationToken);

    private async Task RefreshVoiceLabCatalogObservedAsync(Guid refreshId, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _voiceLabCatalogRefreshInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A Voice Lab catalog refresh is already in progress.");

        OnPropertyChanged(nameof(CanRefreshVoiceLabCatalog));
        RefreshVoiceLabCatalogCommand.NotifyCanExecuteChanged();

        try
        {
            VoiceLabCatalogLastRefreshSucceeded = false;
            VoiceLabCatalogSelection? previousSelection = SelectedVoiceLabVoice;
            SelectedVoiceLabVoice = null;
            VoiceLabCatalogResults.Clear();
            var catalog = _voiceLabCatalog
                ?? throw new InvalidOperationException("Voice Lab catalog is not configured.");
            var capture = _captureVoiceLabCatalogState
                ?? throw new InvalidOperationException("Voice Lab catalog UI state capture is not configured.");

            cancellationToken.ThrowIfCancellationRequested();
            var state = await capture(cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Voice Lab catalog UI state is unavailable.");
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

            cancellationToken.ThrowIfCancellationRequested();
            SelectedVoiceLabVoice = previousSelection is null ? null :
                VoiceLabCatalogResults.FirstOrDefault(candidate => SameVoiceTrustIdentity(candidate, previousSelection));

            RestorePreferredCatalogVoice();
            VoiceLabCatalogLastRefreshSucceeded = true;

            StatusMessage = $"Voice Lab · {VoiceLabCatalogResults.Count} trusted voices";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            SelectedVoiceLabVoice = null;
            VoiceLabCatalogResults.Clear();
            GoogleCatalogRefreshDiagnostics.Failure(_logger, refreshId, GoogleCatalogRefreshPhase.UiQuery, exception);
            StatusMessage = VoiceCatalogErrorMessages.For(exception, GoogleCatalogRefreshPhase.UiQuery);
        }
        finally
        {
            Volatile.Write(ref _voiceLabCatalogRefreshInFlight, 0);
            OnPropertyChanged(nameof(CanRefreshVoiceLabCatalog));
            RefreshVoiceLabCatalogCommand.NotifyCanExecuteChanged();
            RefreshVoiceLabRouteAction();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAuditionSelectedVoice))]
    private async Task AuditionSelectedVoiceAsync(CancellationToken cancellationToken)
    {
        lock (_googleCredentialOperationGate)
        {
            if (Volatile.Read(ref _googleCredentialRepairActive) != 0)
                throw new InvalidOperationException("Credential repair must finish before auditions.");
            if (Interlocked.CompareExchange(ref _voiceLabAuditionInFlight, 1, 0) != 0)
                throw new InvalidOperationException("A Voice Lab audition is already in progress.");
        }

        OnPropertyChanged(nameof(CanAuditionSelectedVoice));
        AuditionSelectedVoiceCommand.NotifyCanExecuteChanged();

        try
        {
            var selected = SelectedVoiceLabVoice?.Validate()
                ?? throw new InvalidOperationException("No trusted Voice Lab selection is available for audition.");
            var create = _createVoiceLabAuditionService
                ?? throw new InvalidOperationException("Voice Lab audition service is not configured.");
            var capture = _captureVoiceLabAuditionRequest
                ?? throw new InvalidOperationException("Voice Lab audition request capture is not configured.");
            var refresh = _refreshVoiceLabSelection
                ?? throw new InvalidOperationException("Voice Lab selection refresh is not configured.");

            cancellationToken.ThrowIfCancellationRequested();
            var request = await capture(selected, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Voice Lab audition request is unavailable.");
            var currentSelection = await refresh(selected, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Voice Lab current selection evidence is unavailable.");
            currentSelection.Validate();

            cancellationToken.ThrowIfCancellationRequested();
            var service = await create(selected, request, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Voice Lab audition service factory returned no service.");
            var outcome = await service.ExecuteWithCurrentSelectionAsync(
                request,
                currentSelection,
                cancellationToken).ConfigureAwait(true);

            StatusMessage = outcome.CacheHit
                ? "Voice Lab · trusted audition cache hit"
                : "Voice Lab · trusted audition generated";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Voice Lab audition cancelled · review queue and spend status before another audition";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            // Failure can follow a provider attempt. Do not retry or expose request details.
            StatusMessage = "Voice Lab audition stopped · review queue and spend status before preparing another audition";
        }
        finally
        {
            Volatile.Write(ref _voiceLabAuditionInFlight, 0);
            OnPropertyChanged(nameof(CanAuditionSelectedVoice));
            AuditionSelectedVoiceCommand.NotifyCanExecuteChanged();
            RefreshVoiceLabRouteAction();
        }
    }

    private static bool SameVoiceTrustIdentity(VoiceLabCatalogSelection left, VoiceLabCatalogSelection right) =>
        string.Equals(left.ProviderStableId, right.ProviderStableId, StringComparison.Ordinal) &&
        string.Equals(left.AccountStableId, right.AccountStableId, StringComparison.Ordinal) &&
        string.Equals(left.ProjectStableId, right.ProjectStableId, StringComparison.Ordinal) &&
        string.Equals(left.VoiceStableId, right.VoiceStableId, StringComparison.Ordinal) &&
        string.Equals(left.VoiceFingerprint, right.VoiceFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.CapabilityEvidenceId, right.CapabilityEvidenceId, StringComparison.Ordinal);
}

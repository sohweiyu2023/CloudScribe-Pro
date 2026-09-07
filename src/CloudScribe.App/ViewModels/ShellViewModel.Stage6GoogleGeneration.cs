using CloudScribe.App.Navigation;
using CloudScribe.Application.Generation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>>? _resolveGoogleGenerationExecutionContext;
    private Func<CancellationToken, Task>? _prepareGoogleGenerationForApproval;
    private Func<long, bool, CancellationToken, Task>? _approveGoogleGenerationSpend;
    private Func<GoogleGenerationQueueOutcome, CancellationToken, Task<string>>? _persistAcceptedGoogleMp3;
    private Func<string, CancellationToken, Task>? _playVerifiedGoogleMp3;
    private string? _lastGeneratedGoogleMp3Path;
    private int _googleGenerationInFlight;

    public bool CanGenerateWithGoogle =>
        _resolveGoogleGenerationExecutionContext is not null &&
        _persistAcceptedGoogleMp3 is not null &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public bool CanApproveGoogleGenerationSpend =>
        _prepareGoogleGenerationForApproval is not null &&
        _approveGoogleGenerationSpend is not null &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public string? LastGeneratedGoogleMp3Path
    {
        get => _lastGeneratedGoogleMp3Path;
        private set
        {
            if (!SetProperty(ref _lastGeneratedGoogleMp3Path, value))
                return;
            OnPropertyChanged(nameof(CanPlayLastGeneratedGoogleMp3));
            PlayLastGeneratedGoogleMp3Command.NotifyCanExecuteChanged();
        }
    }

    public bool CanPlayLastGeneratedGoogleMp3 =>
        _playVerifiedGoogleMp3 is not null &&
        !string.IsNullOrWhiteSpace(LastGeneratedGoogleMp3Path) &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public void ConfigureStage6GoogleGeneration(
        Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>> resolveCurrentExecutionContext)
    {
        _resolveGoogleGenerationExecutionContext = resolveCurrentExecutionContext
            ?? throw new ArgumentNullException(nameof(resolveCurrentExecutionContext));
        RefreshGoogleGenerationCommands();
        RefreshGoogleGenerationRouteAction();
    }

    public void ConfigureStage6GoogleGenerationPreparation(
        Func<CancellationToken, Task> prepareCurrentRequestForApproval)
    {
        _prepareGoogleGenerationForApproval = prepareCurrentRequestForApproval
            ?? throw new ArgumentNullException(nameof(prepareCurrentRequestForApproval));
        OnPropertyChanged(nameof(CanApproveGoogleGenerationSpend));
    }

    public void ConfigureStage6GoogleGenerationSpendApproval(
        Func<long, bool, CancellationToken, Task> approveExplicitSpend)
    {
        _approveGoogleGenerationSpend = approveExplicitSpend
            ?? throw new ArgumentNullException(nameof(approveExplicitSpend));
        OnPropertyChanged(nameof(CanApproveGoogleGenerationSpend));
    }

    public void ConfigureStage6GoogleGenerationOutput(
        Func<GoogleGenerationQueueOutcome, CancellationToken, Task<string>> persistAcceptedMp3,
        Func<string, CancellationToken, Task> playVerifiedMp3)
    {
        _persistAcceptedGoogleMp3 = persistAcceptedMp3
            ?? throw new ArgumentNullException(nameof(persistAcceptedMp3));
        _playVerifiedGoogleMp3 = playVerifiedMp3
            ?? throw new ArgumentNullException(nameof(playVerifiedMp3));
        RefreshGoogleGenerationCommands();
    }

    public async Task ApproveGoogleGenerationSpendAsync(
        long authorizedMaximumMinorUnits,
        bool confirmedByUser,
        CancellationToken cancellationToken = default)
    {
        EnterGoogleGenerationOperation();
        try
        {
            var prepare = _prepareGoogleGenerationForApproval
                ?? throw new InvalidOperationException("Google generation production preparation is not configured.");
            var approve = _approveGoogleGenerationSpend
                ?? throw new InvalidOperationException("Google generation explicit spend approval is not configured.");
            cancellationToken.ThrowIfCancellationRequested();
            StatusMessage = "Google generation · compiling exact current request for approval";
            await prepare(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            StatusMessage = "Google generation · confirming exact compiled spend authorization";
            await approve(authorizedMaximumMinorUnits, confirmedByUser, cancellationToken).ConfigureAwait(true);
            StatusMessage = "Google generation · exact compiled spend authorized";
        }
        finally
        {
            ExitGoogleGenerationOperation();
        }
    }

    private void RefreshGoogleGenerationRouteAction()
    {
        if (!_pages.TryGetValue(AppRoute.Studio, out RoutePageViewModel? page) || page is null)
            return;
        if (_resolveGoogleGenerationExecutionContext is null)
            return;
        page.HasPrimaryAction = true;
        page.PrimaryActionLabel = "Generate with Google";
        page.PrimaryActionCommand = GenerateWithGoogleCommand;
    }

    [RelayCommand(CanExecute = nameof(CanGenerateWithGoogle))]
    private async Task GenerateWithGoogleAsync(CancellationToken cancellationToken)
    {
        EnterGoogleGenerationOperation();
        try
        {
            GoogleGenerationUiExecutionContext executionContext = await ResolveGoogleExecutionContextAsync(cancellationToken)
                .ConfigureAwait(true);
            var coordinator = executionContext.Coordinator
                ?? throw new InvalidOperationException("Google generation coordinator is unavailable for the current authorization state.");
            var state = executionContext.Snapshot
                ?? throw new InvalidOperationException("Google generation UI state is unavailable.");
            StatusMessage = "Google generation · validating current account, voice, pricing and trust";
            GoogleGenerationQueueOutcome outcome = await coordinator.ProcessPersistedTransitionAsync(
                state.UiSelection, state.AccountAuthorized, state.ProjectAuthorized, state.CapabilityCurrent,
                state.PricingCurrent, state.ProviderRequest, state.AdmittedTrust, state.PreviousState,
                state.CurrentState, state.ResolutionEvidence, state.AdmissionCurrent,
                state.AccountCredentialAvailable, state.PricingApproved, state.PostCompileLimitsSatisfied,
                cancellationToken).ConfigureAwait(true);
            await HandleGoogleGenerationOutcomeAsync(outcome, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            ExitGoogleGenerationOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlayLastGeneratedGoogleMp3))]
    private async Task PlayLastGeneratedGoogleMp3Async(CancellationToken cancellationToken)
    {
        string path = LastGeneratedGoogleMp3Path
            ?? throw new InvalidOperationException("No verified Google MP3 output is available.");
        var play = _playVerifiedGoogleMp3
            ?? throw new InvalidOperationException("Google MP3 playback is not configured.");
        await play(path, cancellationToken).ConfigureAwait(true);
        StatusMessage = $"Google generation · opened verified MP3 · {path}";
    }

    private async Task<GoogleGenerationUiExecutionContext> ResolveGoogleExecutionContextAsync(
        CancellationToken cancellationToken)
    {
        var resolveContext = _resolveGoogleGenerationExecutionContext
            ?? throw new InvalidOperationException("Google generation execution context resolution is not configured.");
        cancellationToken.ThrowIfCancellationRequested();
        return await resolveContext(cancellationToken).ConfigureAwait(true)
            ?? throw new InvalidOperationException("Google generation execution context is unavailable for the current authorization state.");
    }

    private async Task HandleGoogleGenerationOutcomeAsync(
        GoogleGenerationQueueOutcome outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.RequiresReconciliation)
        {
            StatusMessage = "Google generation · reconciliation required; no output file exposed";
            return;
        }
        if (outcome.Response?.IsAccepted != true)
        {
            StatusMessage = $"Google generation · not accepted · {outcome.Decision.Reason}";
            return;
        }
        var persist = _persistAcceptedGoogleMp3
            ?? throw new InvalidOperationException("Verified Google MP3 persistence is not configured.");
        LastGeneratedGoogleMp3Path = await persist(outcome, cancellationToken).ConfigureAwait(true);
        StatusMessage = $"Google generation · verified MP3 ready · {LastGeneratedGoogleMp3Path}";
    }

    private void EnterGoogleGenerationOperation()
    {
        if (Interlocked.CompareExchange(ref _googleGenerationInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A Google generation approval or submission is already in progress.");
        RefreshGoogleGenerationCommands();
    }

    private void ExitGoogleGenerationOperation()
    {
        Volatile.Write(ref _googleGenerationInFlight, 0);
        RefreshGoogleGenerationCommands();
    }

    private void RefreshGoogleGenerationCommands()
    {
        OnPropertyChanged(nameof(CanGenerateWithGoogle));
        OnPropertyChanged(nameof(CanApproveGoogleGenerationSpend));
        OnPropertyChanged(nameof(CanPlayLastGeneratedGoogleMp3));
        GenerateWithGoogleCommand.NotifyCanExecuteChanged();
        PlayLastGeneratedGoogleMp3Command.NotifyCanExecuteChanged();
    }
}

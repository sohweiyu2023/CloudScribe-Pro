using CloudScribe.App.Navigation;
using CloudScribe.Application.Generation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private GoogleGenerationUiQueueCoordinator? _googleGenerationUiQueue;
    private Func<CancellationToken, Task<GoogleGenerationUiExecutionSnapshot>>? _captureGoogleGenerationState;
    private int _googleGenerationInFlight;

    public bool CanGenerateWithGoogle =>
        _googleGenerationUiQueue is not null &&
        _captureGoogleGenerationState is not null &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public void ConfigureStage6GoogleGeneration(
        GoogleGenerationUiQueueCoordinator coordinator,
        Func<CancellationToken, Task<GoogleGenerationUiExecutionSnapshot>> captureCurrentState)
    {
        _googleGenerationUiQueue = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _captureGoogleGenerationState = captureCurrentState ?? throw new ArgumentNullException(nameof(captureCurrentState));
        OnPropertyChanged(nameof(CanGenerateWithGoogle));
        GenerateWithGoogleCommand.NotifyCanExecuteChanged();
        RefreshGoogleGenerationRouteAction();
    }

    private void RefreshGoogleGenerationRouteAction()
    {
        if (!_pages.TryGetValue(AppRoute.Studio, out RoutePageViewModel? page) || page is null)
        {
            return;
        }

        if (_googleGenerationUiQueue is null || _captureGoogleGenerationState is null)
        {
            return;
        }

        page.HasPrimaryAction = true;
        page.PrimaryActionLabel = "Generate with Google";
        page.PrimaryActionCommand = GenerateWithGoogleCommand;
    }

    [RelayCommand(CanExecute = nameof(CanGenerateWithGoogle))]
    private async Task GenerateWithGoogleAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _googleGenerationInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A Google generation request is already in progress.");
        }

        OnPropertyChanged(nameof(CanGenerateWithGoogle));
        GenerateWithGoogleCommand.NotifyCanExecuteChanged();

        try
        {
            var coordinator = _googleGenerationUiQueue
                ?? throw new InvalidOperationException("Google generation is not configured.");
            var capture = _captureGoogleGenerationState
                ?? throw new InvalidOperationException("Google generation UI state capture is not configured.");

            cancellationToken.ThrowIfCancellationRequested();
            var state = await capture(cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Google generation UI state is unavailable.");

            StatusMessage = "Google generation · validating current account, voice, pricing and trust";
            await coordinator.ProcessPersistedTransitionAsync(
                state.UiSelection,
                state.AccountAuthorized,
                state.ProjectAuthorized,
                state.CapabilityCurrent,
                state.PricingCurrent,
                state.ProviderRequest,
                state.AdmittedTrust,
                state.PreviousState,
                state.CurrentState,
                state.ResolutionEvidence,
                state.AdmissionCurrent,
                state.AccountCredentialAvailable,
                state.PricingApproved,
                state.PostCompileLimitsSatisfied,
                cancellationToken).ConfigureAwait(true);

            StatusMessage = "Google generation · queued with current v2.23 trust";
        }
        finally
        {
            Volatile.Write(ref _googleGenerationInFlight, 0);
            OnPropertyChanged(nameof(CanGenerateWithGoogle));
            GenerateWithGoogleCommand.NotifyCanExecuteChanged();
        }
    }
}

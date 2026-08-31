using CloudScribe.App.Navigation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>>? _resolveGoogleGenerationExecutionContext;
    private int _googleGenerationInFlight;

    public bool CanGenerateWithGoogle =>
        _resolveGoogleGenerationExecutionContext is not null &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public void ConfigureStage6GoogleGeneration(
        Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>> resolveCurrentExecutionContext)
    {
        _resolveGoogleGenerationExecutionContext = resolveCurrentExecutionContext
            ?? throw new ArgumentNullException(nameof(resolveCurrentExecutionContext));
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

        if (_resolveGoogleGenerationExecutionContext is null)
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
            var resolveContext = _resolveGoogleGenerationExecutionContext
                ?? throw new InvalidOperationException("Google generation execution context resolution is not configured.");

            cancellationToken.ThrowIfCancellationRequested();
            var executionContext = await resolveContext(cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Google generation execution context is unavailable for the current authorization state.");
            var coordinator = executionContext.Coordinator
                ?? throw new InvalidOperationException("Google generation coordinator is unavailable for the current authorization state.");
            var state = executionContext.Snapshot
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

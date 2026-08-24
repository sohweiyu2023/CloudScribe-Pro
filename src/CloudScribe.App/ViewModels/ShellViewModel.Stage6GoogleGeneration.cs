using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed record GoogleGenerationUiExecutionSnapshot(
    GoogleGenerationUiSelection UiSelection,
    bool AccountAuthorized,
    bool ProjectAuthorized,
    bool CapabilityCurrent,
    bool PricingCurrent,
    GenerationProviderRequest ProviderRequest,
    GenerationCacheTrustContext AdmittedTrust,
    GoogleGenerationPersistedQueueState PreviousState,
    GoogleGenerationPersistedQueueState CurrentState,
    GoogleGenerationReconciliationResolutionEvidence ResolutionEvidence,
    bool AdmissionCurrent,
    bool AccountCredentialAvailable,
    bool PricingApproved,
    bool PostCompileLimitsSatisfied);

public sealed partial class ShellViewModel
{
    private GoogleGenerationUiQueueCoordinator? _googleGenerationUiQueue;
    private Func<GoogleGenerationUiExecutionSnapshot>? _captureGoogleGenerationState;

    public bool CanGenerateWithGoogle =>
        _googleGenerationUiQueue is not null && _captureGoogleGenerationState is not null;

    public void ConfigureStage6GoogleGeneration(
        GoogleGenerationUiQueueCoordinator coordinator,
        Func<GoogleGenerationUiExecutionSnapshot> captureCurrentState)
    {
        _googleGenerationUiQueue = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _captureGoogleGenerationState = captureCurrentState ?? throw new ArgumentNullException(nameof(captureCurrentState));
        OnPropertyChanged(nameof(CanGenerateWithGoogle));
        GenerateWithGoogleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGenerateWithGoogle))]
    private async Task GenerateWithGoogleAsync(CancellationToken cancellationToken)
    {
        var coordinator = _googleGenerationUiQueue
            ?? throw new InvalidOperationException("Google generation is not configured.");
        var capture = _captureGoogleGenerationState
            ?? throw new InvalidOperationException("Google generation UI state capture is not configured.");

        cancellationToken.ThrowIfCancellationRequested();
        var state = capture() ?? throw new InvalidOperationException("Google generation UI state is unavailable.");

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
}

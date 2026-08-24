using CloudScribe.Application.Safety;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private RestoreRecoveryCoordinator? _restoreRecoveryCoordinator;
    private Func<RestoreRecoveryState>? _captureRestoreRecoveryState;
    private Func<string, CancellationToken, Task<bool>>? _verifyRestoreRecoveryCompletion;
    private int _restoreRecoveryInFlight;

    public bool CanRecoverInterruptedRestore =>
        _restoreRecoveryCoordinator is not null &&
        _captureRestoreRecoveryState is not null &&
        _verifyRestoreRecoveryCompletion is not null &&
        Volatile.Read(ref _restoreRecoveryInFlight) == 0;

    public void ConfigureStage8RestoreRecovery(
        RestoreRecoveryCoordinator coordinator,
        Func<RestoreRecoveryState> captureCurrentState,
        Func<string, CancellationToken, Task<bool>> verifyCompletedActionAsync)
    {
        _restoreRecoveryCoordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _captureRestoreRecoveryState = captureCurrentState ?? throw new ArgumentNullException(nameof(captureCurrentState));
        _verifyRestoreRecoveryCompletion = verifyCompletedActionAsync ?? throw new ArgumentNullException(nameof(verifyCompletedActionAsync));
        OnPropertyChanged(nameof(CanRecoverInterruptedRestore));
        RecoverInterruptedRestoreCommand.NotifyCanExecuteChanged();
        RefreshRestoreRecoveryRouteAction();
    }

    private void RefreshRestoreRecoveryRouteAction()
    {
        if (!_pages.TryGetValue(AppRoute.Settings, out RoutePageViewModel? page))
        {
            return;
        }

        if (_restoreRecoveryCoordinator is null ||
            _captureRestoreRecoveryState is null ||
            _verifyRestoreRecoveryCompletion is null)
        {
            return;
        }

        page.HasPrimaryAction = true;
        page.PrimaryActionLabel = "Recover interrupted restore";
        page.PrimaryActionCommand = RecoverInterruptedRestoreCommand;
    }

    [RelayCommand(CanExecute = nameof(CanRecoverInterruptedRestore))]
    private async Task RecoverInterruptedRestoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _restoreRecoveryInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("Restore recovery is already in progress.");
        }

        OnPropertyChanged(nameof(CanRecoverInterruptedRestore));
        RecoverInterruptedRestoreCommand.NotifyCanExecuteChanged();

        try
        {
            var coordinator = _restoreRecoveryCoordinator
                ?? throw new InvalidOperationException("Restore recovery is not configured.");
            var capture = _captureRestoreRecoveryState
                ?? throw new InvalidOperationException("Restore recovery state capture is not configured.");
            var verify = _verifyRestoreRecoveryCompletion
                ?? throw new InvalidOperationException("Restore recovery completion verification is not configured.");

            cancellationToken.ThrowIfCancellationRequested();
            var state = capture() ?? throw new InvalidOperationException("Restore recovery state is unavailable.");
            StatusMessage = "Restore recovery · verifying journal and filesystem state";

            var outcome = await coordinator.RecoverVerifiedAsync(state, verify, cancellationToken).ConfigureAwait(true);
            StatusMessage = outcome switch
            {
                "rollback-completed" => "Restore recovery · rollback verified",
                "verified-apply-resumed" => "Restore recovery · verified apply resumed",
                "no-op-terminal-rolled-back" => "Restore recovery · already rolled back and verified",
                _ => throw new InvalidOperationException("Restore recovery returned an unknown verified outcome."),
            };
        }
        finally
        {
            Volatile.Write(ref _restoreRecoveryInFlight, 0);
            OnPropertyChanged(nameof(CanRecoverInterruptedRestore));
            RecoverInterruptedRestoreCommand.NotifyCanExecuteChanged();
        }
    }
}

using CloudScribe.Application.Safety;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private RestoreRecoveryCoordinator? _restoreRecoveryCoordinator;
    private Func<RestoreRecoveryState>? _captureRestoreRecoveryState;
    private Func<string, CancellationToken, Task<bool>>? _verifyRestoreRecoveryCompletion;

    public bool CanRecoverInterruptedRestore =>
        _restoreRecoveryCoordinator is not null &&
        _captureRestoreRecoveryState is not null &&
        _verifyRestoreRecoveryCompletion is not null;

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
    }

    [RelayCommand(CanExecute = nameof(CanRecoverInterruptedRestore))]
    private async Task RecoverInterruptedRestoreAsync(CancellationToken cancellationToken)
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
}

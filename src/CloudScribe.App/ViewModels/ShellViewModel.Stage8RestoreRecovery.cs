using CloudScribe.App.Navigation;
using CloudScribe.Application.Safety;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<CancellationToken, Task<string?>>? _recoverInterruptedRestore;
    private int _restoreRecoveryInFlight;

    public bool CanRecoverInterruptedRestore =>
        _recoverInterruptedRestore is not null &&
        Volatile.Read(ref _restoreRecoveryInFlight) == 0;

    public void ConfigureStage8RestoreRecovery(
        Func<CancellationToken, Task<string?>> recoverVerifiedAsync)
    {
        _recoverInterruptedRestore = recoverVerifiedAsync
            ?? throw new ArgumentNullException(nameof(recoverVerifiedAsync));
        OnPropertyChanged(nameof(CanRecoverInterruptedRestore));
        RecoverInterruptedRestoreCommand.NotifyCanExecuteChanged();
        RefreshRestoreRecoveryRouteAction();
    }

    public void ConfigureStage8RestoreRecovery(
        RestoreRecoveryCoordinator coordinator,
        Func<CancellationToken, Task<RestoreRecoveryState>> captureCurrentStateAsync,
        Func<string, CancellationToken, Task<bool>> verifyCompletedActionAsync)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(captureCurrentStateAsync);
        ArgumentNullException.ThrowIfNull(verifyCompletedActionAsync);

        ConfigureStage8RestoreRecovery(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreRecoveryState state = await captureCurrentStateAsync(cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Restore recovery state is unavailable.");
            return await coordinator
                .RecoverVerifiedAsync(state, verifyCompletedActionAsync, cancellationToken)
                .ConfigureAwait(true);
        });
    }

    private void RefreshRestoreRecoveryRouteAction()
    {
        if (!_pages.TryGetValue(AppRoute.Settings, out RoutePageViewModel? page) || page is null)
        {
            return;
        }

        if (_recoverInterruptedRestore is null)
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
            Func<CancellationToken, Task<string?>> recover = _recoverInterruptedRestore
                ?? throw new InvalidOperationException("Restore recovery is not configured.");

            cancellationToken.ThrowIfCancellationRequested();
            StatusMessage = "Restore recovery · verifying journal and filesystem state";

            string? outcome = await recover(cancellationToken).ConfigureAwait(true);
            StatusMessage = outcome switch
            {
                null => "Restore recovery · no interrupted restore found",
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

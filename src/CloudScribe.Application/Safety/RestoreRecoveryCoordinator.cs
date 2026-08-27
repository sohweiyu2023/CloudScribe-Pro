namespace CloudScribe.Application.Safety;

public sealed class RestoreRecoveryCoordinator
{
    private readonly Func<CancellationToken, Task> _rollbackAsync;
    private readonly Func<CancellationToken, Task> _resumeVerifiedApplyAsync;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    public RestoreRecoveryCoordinator(
        Func<CancellationToken, Task> rollbackAsync,
        Func<CancellationToken, Task> resumeVerifiedApplyAsync)
    {
        _rollbackAsync = rollbackAsync ?? throw new ArgumentNullException(nameof(rollbackAsync));
        _resumeVerifiedApplyAsync = resumeVerifiedApplyAsync ?? throw new ArgumentNullException(nameof(resumeVerifiedApplyAsync));
    }

    public async Task<string> RecoverVerifiedAsync(
        RestoreRecoveryState state,
        Func<string, CancellationToken, Task<bool>> verifyCompletedActionAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifyCompletedActionAsync);
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Concurrent restore recovery is forbidden; reconcile the in-flight recovery action first.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(state);
            string outcome;
            switch (action)
            {
                case "rollback-only":
                    await _rollbackAsync(cancellationToken).ConfigureAwait(false);
                    outcome = "rollback-completed";
                    break;
                case "resume-verified-apply":
                    await _resumeVerifiedApplyAsync(cancellationToken).ConfigureAwait(false);
                    outcome = "verified-apply-resumed";
                    break;
                case "no-op-terminal-rolled-back":
                    outcome = action;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown admitted restore recovery action: {action}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await verifyCompletedActionAsync(outcome, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Restore recovery action completed without verifiable terminal/reconciled state.");

            // A cancellation arriving during terminal verification must not be converted
            // into a successful recovery result after the caller has abandoned the action.
            cancellationToken.ThrowIfCancellationRequested();
            return outcome;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }
}

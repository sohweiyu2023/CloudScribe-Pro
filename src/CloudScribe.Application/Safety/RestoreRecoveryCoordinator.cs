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

    public async Task<string> RecoverAsync(
        RestoreRecoveryState state,
        CancellationToken cancellationToken = default)
    {
        if (!await _recoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Concurrent restore recovery is forbidden; reconcile the in-flight recovery action first.");

        try
        {
            var action = RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(state);
            switch (action)
            {
                case "rollback-only":
                    await _rollbackAsync(cancellationToken).ConfigureAwait(false);
                    return "rollback-completed";
                case "resume-verified-apply":
                    await _resumeVerifiedApplyAsync(cancellationToken).ConfigureAwait(false);
                    return "verified-apply-resumed";
                case "no-op-terminal-rolled-back":
                    return action;
                default:
                    throw new InvalidOperationException($"Unknown admitted restore recovery action: {action}");
            }
        }
        finally
        {
            _recoveryGate.Release();
        }
    }
}

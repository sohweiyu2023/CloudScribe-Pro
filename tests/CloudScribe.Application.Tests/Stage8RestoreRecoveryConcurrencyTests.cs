using CloudScribe.Application.Safety;

namespace CloudScribe.Application.Tests;

public sealed class Stage8RestoreRecoveryConcurrencyTests
{
    [Fact]
    public async Task Concurrent_recovery_attempt_is_rejected_and_success_is_verified()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationCalls = 0;

        var coordinator = new RestoreRecoveryCoordinator(
            async _ =>
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            },
            _ => Task.CompletedTask);

        var rollbackState = new RestoreRecoveryState(
            JournalAuthenticated: true,
            PlanIdentityMatches: true,
            StagingRootTrusted: true,
            DestinationRootTrusted: true,
            RollbackRequired: true,
            AlreadyRolledBack: false);

        Task<bool> VerifyAsync(string outcome, CancellationToken _)
        {
            verificationCalls++;
            return Task.FromResult(outcome == "rollback-completed");
        }

        var first = coordinator.RecoverVerifiedAsync(rollbackState, VerifyAsync);
        await entered.Task.ConfigureAwait(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecoverVerifiedAsync(rollbackState, VerifyAsync));

        release.SetResult();
        Assert.Equal("rollback-completed", await first.ConfigureAwait(false));
        Assert.Equal(1, verificationCalls);
    }

    [Fact]
    public async Task Recovery_action_cannot_report_success_when_terminal_verification_fails()
    {
        var coordinator = new RestoreRecoveryCoordinator(_ => Task.CompletedTask, _ => Task.CompletedTask);
        var rollbackState = new RestoreRecoveryState(true, true, true, true, true, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecoverVerifiedAsync(
            rollbackState,
            static (_, _) => Task.FromResult(false)));
    }

    [Fact]
    public async Task Already_rolled_back_no_op_still_requires_terminal_filesystem_verification()
    {
        var rollbackCalls = 0;
        var resumeCalls = 0;
        var verificationCalls = 0;
        var coordinator = new RestoreRecoveryCoordinator(
            _ =>
            {
                rollbackCalls++;
                return Task.CompletedTask;
            },
            _ =>
            {
                resumeCalls++;
                return Task.CompletedTask;
            });
        var terminalState = new RestoreRecoveryState(true, true, true, true, false, true);

        var outcome = await coordinator.RecoverVerifiedAsync(
            terminalState,
            (candidate, _) =>
            {
                verificationCalls++;
                return Task.FromResult(candidate == "no-op-terminal-rolled-back");
            });

        Assert.Equal("no-op-terminal-rolled-back", outcome);
        Assert.Equal(0, rollbackCalls);
        Assert.Equal(0, resumeCalls);
        Assert.Equal(1, verificationCalls);
    }

    [Fact]
    public async Task Cancellation_during_terminal_verification_cannot_return_success()
    {
        using var cancellation = new CancellationTokenSource();
        var coordinator = new RestoreRecoveryCoordinator(_ => Task.CompletedTask, _ => Task.CompletedTask);
        var terminalState = new RestoreRecoveryState(true, true, true, true, false, true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RecoverVerifiedAsync(
            terminalState,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            cancellation.Token));
    }
}

using CloudScribe.Application.Safety;

namespace CloudScribe.Application.Tests;

public sealed class Stage8RestoreRecoveryConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRecoveryAttemptIsRejectedAndSuccessIsVerified()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationCalls = 0;
        var cancellationToken = TestContext.Current.CancellationToken;

        var coordinator = new RestoreRecoveryCoordinator(
            _ =>
            {
                entered.SetResult();
                return release.Task;
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
            return Task.FromResult(string.Equals(outcome, "rollback-completed", StringComparison.Ordinal));
        }

        var first = coordinator.RecoverVerifiedAsync(rollbackState, VerifyAsync, cancellationToken);
        await entered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RecoverVerifiedAsync(rollbackState, VerifyAsync, cancellationToken));

        release.SetResult();
        Assert.Equal("rollback-completed", await first);
        Assert.Equal(1, verificationCalls);
    }

    [Fact]
    public async Task RecoveryActionCannotReportSuccessWhenTerminalVerificationFails()
    {
        var coordinator = new RestoreRecoveryCoordinator(_ => Task.CompletedTask, _ => Task.CompletedTask);
        var rollbackState = new RestoreRecoveryState(true, true, true, true, true, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecoverVerifiedAsync(
            rollbackState,
            static (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AlreadyRolledBackNoOpStillRequiresTerminalFilesystemVerification()
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
                return Task.FromResult(string.Equals(candidate, "no-op-terminal-rolled-back", StringComparison.Ordinal));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("no-op-terminal-rolled-back", outcome);
        Assert.Equal(0, rollbackCalls);
        Assert.Equal(0, resumeCalls);
        Assert.Equal(1, verificationCalls);
    }

    [Fact]
    public async Task CancellationDuringTerminalVerificationCannotReturnSuccess()
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

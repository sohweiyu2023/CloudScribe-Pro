using CloudScribe.Application.Safety;

namespace CloudScribe.Application.Tests;

public sealed class Stage8RestoreRecoveryConcurrencyTests
{
    [Fact]
    public async Task Concurrent_recovery_attempt_is_rejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

        var first = coordinator.RecoverAsync(rollbackState);
        await entered.Task.ConfigureAwait(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecoverAsync(rollbackState));

        release.SetResult();
        Assert.Equal("rollback-completed", await first.ConfigureAwait(false));
    }
}

using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class RestoreRecoveryStateResolverTests
{
    [Fact]
    public void ConstructorRejectsRelativeStagingRootInsteadOfUsingAmbientWorkingDirectory()
    {
        string root = CreateTemporaryRoot();
        try
        {
            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(
                Path.Combine(root, "recovery.journal"),
                CreateAuthenticationKey());

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => new RestoreRecoveryStateResolver(store, "relative-staging-root"));

            Assert.Contains("explicitly fully qualified", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ResolveDerivesRollbackOnlyFromAuthenticatedBoundJournal()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string stagingRoot = Path.Combine(root, "staging");
            string restoreRoot = Path.Combine(root, "destination");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(restoreRoot);
            RestoreExecutionPlan plan = CreatePlan(restoreRoot);
            var journal = new RestoreTransactionJournal(
                Guid.NewGuid(),
                RestoreTransactionJournal.ComputePlanSha256(plan),
                RestoreTransactionState.RollbackRequired,
                [plan.Steps[0].RelativePath],
                new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));

            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(
                Path.Combine(root, "recovery.journal"),
                CreateAuthenticationKey());
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await store.SaveAsync(journal, cancellationToken);
            var resolver = new RestoreRecoveryStateResolver(store, stagingRoot);

            RestoreRecoveryContext? context = await resolver.ResolveAsync(plan, cancellationToken);

            Assert.NotNull(context);
            Assert.Equal(journal, context.Journal);
            Assert.True(context.State.JournalAuthenticated);
            Assert.True(context.State.PlanIdentityMatches);
            Assert.True(context.State.StagingRootTrusted);
            Assert.True(context.State.DestinationRootTrusted);
            Assert.True(context.State.RollbackRequired);
            Assert.False(context.State.AlreadyRolledBack);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ResolveRejectsAuthenticatedJournalBoundToDifferentPlan()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string stagingRoot = Path.Combine(root, "staging");
            string restoreRoot = Path.Combine(root, "destination");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(restoreRoot);
            RestoreExecutionPlan expectedPlan = CreatePlan(restoreRoot);
            RestoreExecutionPlan otherPlan = CreatePlan(Path.Combine(root, "other-destination"));
            var journal = new RestoreTransactionJournal(
                Guid.NewGuid(),
                RestoreTransactionJournal.ComputePlanSha256(otherPlan),
                RestoreTransactionState.Copying,
                Array.Empty<string>(),
                new DateTimeOffset(2026, 8, 31, 0, 1, 0, TimeSpan.Zero));

            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(
                Path.Combine(root, "recovery.journal"),
                CreateAuthenticationKey());
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await store.SaveAsync(journal, cancellationToken);
            var resolver = new RestoreRecoveryStateResolver(store, stagingRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => resolver.ResolveAsync(expectedPlan, cancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ResolveRejectsCommittedJournalAsNonRecoverableTerminalState()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string stagingRoot = Path.Combine(root, "staging");
            string restoreRoot = Path.Combine(root, "destination");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(restoreRoot);
            RestoreExecutionPlan plan = CreatePlan(restoreRoot);
            var journal = new RestoreTransactionJournal(
                Guid.NewGuid(),
                RestoreTransactionJournal.ComputePlanSha256(plan),
                RestoreTransactionState.Committed,
                [plan.Steps[0].RelativePath],
                new DateTimeOffset(2026, 8, 31, 0, 2, 0, TimeSpan.Zero));

            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(
                Path.Combine(root, "recovery.journal"),
                CreateAuthenticationKey());
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await store.SaveAsync(journal, cancellationToken);
            var resolver = new RestoreRecoveryStateResolver(store, stagingRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => resolver.ResolveAsync(plan, cancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static RestoreExecutionPlan CreatePlan(string restoreRoot)
    {
        string destination = Path.Combine(restoreRoot, "audio", "part-001.wav");
        var step = new RestoreExecutionStep(
            "audio/part-001.wav",
            destination,
            3,
            new string('a', 64));
        return new RestoreExecutionPlan(Path.GetFullPath(restoreRoot), [step], 3);
    }

    private static byte[] CreateAuthenticationKey()
    {
        var key = new byte[32];
        for (var index = 0; index < key.Length; index++)
            key[index] = checked((byte)(index + 1));
        return key;
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-recovery-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

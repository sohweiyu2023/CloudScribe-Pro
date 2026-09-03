using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.DependencyInjection;
using CloudScribe.Infrastructure.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8IntegratedRecoverySafetyTests
{
    [Fact]
    public async Task AuthenticatedJournalRoundTripsExactPersistedPlanAndRejectsTagTampering()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string restoreRoot = Path.Combine(root, "destination");
            Directory.CreateDirectory(restoreRoot);
            RestoreExecutionPlan plan = CreatePlan(restoreRoot);
            DateTimeOffset startedAt = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
            RestoreTransactionJournal journal = RestoreTransactionJournal
                .Start(plan, startedAt)
                .BeginCopy(plan, startedAt.AddSeconds(1));
            string path = Path.Combine(root, "restore-recovery.journal");
            byte[] key = CreateAuthenticationKey();
            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(path, key);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            Assert.Null(await store.LoadAuthenticatedAsync(cancellationToken));
            await store.SaveAsync(journal, cancellationToken);

            RestoreTransactionJournal? restored = await store.LoadAuthenticatedAsync(cancellationToken);
            Assert.NotNull(restored);
            AssertJournalEquivalent(journal, restored);
            AssertPlanEquivalent(plan, restored.RequirePersistedPlan());

            string document = await File.ReadAllTextAsync(path, cancellationToken);
            string[] lines = document.Split('\n', StringSplitOptions.None);
            Assert.True(lines.Length >= 3);
            char replacement = lines[2][^1] == '0' ? '1' : '0';
            lines[2] = lines[2][..^1] + replacement;
            await File.WriteAllTextAsync(path, string.Join('\n', lines), cancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.LoadAuthenticatedAsync(cancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void StateResolverRejectsRelativeStagingRootInsteadOfUsingAmbientWorkingDirectory()
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
    public async Task StateResolverDerivesRollbackOnlyFromAuthenticatedBoundJournal()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string stagingRoot = Path.Combine(root, "staging");
            string restoreRoot = Path.Combine(root, "destination");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(restoreRoot);
            RestoreExecutionPlan plan = CreatePlan(restoreRoot);
            DateTimeOffset startedAt = new(2026, 9, 3, 0, 1, 0, TimeSpan.Zero);
            RestoreTransactionJournal journal = RestoreTransactionJournal
                .Start(plan, startedAt)
                .BeginCopy(plan, startedAt.AddSeconds(1))
                .RequireRollback(plan, startedAt.AddSeconds(2));

            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(
                Path.Combine(root, "recovery.journal"),
                CreateAuthenticationKey());
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await store.SaveAsync(journal, cancellationToken);
            var resolver = new RestoreRecoveryStateResolver(store, stagingRoot);

            RestoreRecoveryContext? context = await resolver.ResolveAsync(plan, cancellationToken);

            Assert.NotNull(context);
            AssertJournalEquivalent(journal, context.Journal);
            AssertPlanEquivalent(plan, context.Journal.RequirePersistedPlan());
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
    public async Task StateResolverRejectsDifferentPlanAndCommittedTerminalJournal()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string stagingRoot = Path.Combine(root, "staging");
            string expectedRoot = Path.Combine(root, "destination");
            string otherRoot = Path.Combine(root, "other-destination");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(expectedRoot);
            Directory.CreateDirectory(otherRoot);
            RestoreExecutionPlan expectedPlan = CreatePlan(expectedRoot);
            RestoreExecutionPlan otherPlan = CreatePlan(otherRoot);
            DateTimeOffset startedAt = new(2026, 9, 3, 0, 2, 0, TimeSpan.Zero);
            RestoreTransactionJournal otherJournal = RestoreTransactionJournal.Start(otherPlan, startedAt);
            string journalPath = Path.Combine(root, "recovery.journal");

            using (var store = new FileAuthenticatedRestoreRecoveryJournalStore(
                journalPath,
                CreateAuthenticationKey()))
            {
                CancellationToken cancellationToken = TestContext.Current.CancellationToken;
                await store.SaveAsync(otherJournal, cancellationToken);
                var resolver = new RestoreRecoveryStateResolver(store, stagingRoot);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => resolver.ResolveAsync(expectedPlan, cancellationToken));
            }

            File.Delete(journalPath);
            RestoreExecutionPlan committedPlan = expectedPlan;
            RestoreExecutionStep step = committedPlan.Steps[0];
            DateTimeOffset commitStart = startedAt.AddMinutes(1);
            RestoreTransactionJournal committed = RestoreTransactionJournal
                .Start(committedPlan, commitStart)
                .BeginCopy(committedPlan, commitStart.AddSeconds(1))
                .MarkCopied(committedPlan, step.RelativePath, commitStart.AddSeconds(2))
                .BeginVerification(committedPlan, commitStart.AddSeconds(3))
                .Commit(committedPlan, commitStart.AddSeconds(4));

            using var committedStore = new FileAuthenticatedRestoreRecoveryJournalStore(
                journalPath,
                CreateAuthenticationKey());
            CancellationToken committedCancellation = TestContext.Current.CancellationToken;
            await committedStore.SaveAsync(committed, committedCancellation);
            var committedResolver = new RestoreRecoveryStateResolver(committedStore, stagingRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => committedResolver.ResolveAsync(committedPlan, committedCancellation));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void InfrastructureRegistersProductionRecoveryServicesAsSingletons()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddCloudScribeInfrastructure(configuration);

        AssertSingleton<AtomicVerifiedRestoreExecutor>(services);
        AssertSingleton<RestoreRecoveryExecutionCompositionFactory>(services);
        AssertSingleton<RestoreRecoveryProductionRuntime>(services);
    }

    private static void AssertSingleton<TService>(IServiceCollection services)
    {
        ServiceDescriptor descriptor = Assert.Single(
            services,
            static descriptor => descriptor.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(TService), descriptor.ImplementationType);
    }

    private static void AssertJournalEquivalent(
        RestoreTransactionJournal expected,
        RestoreTransactionJournal actual)
    {
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.PlanSha256, actual.PlanSha256);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.CompletedRelativePaths, actual.CompletedRelativePaths);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
    }

    private static void AssertPlanEquivalent(RestoreExecutionPlan expected, RestoreExecutionPlan actual)
    {
        Assert.Equal(expected.RestoreRoot, actual.RestoreRoot);
        Assert.Equal(expected.TotalBytes, actual.TotalBytes);
        Assert.Equal(expected.Steps.Count, actual.Steps.Count);
        for (var index = 0; index < expected.Steps.Count; index++)
        {
            Assert.Equal(expected.Steps[index], actual.Steps[index]);
        }
        Assert.Equal(
            RestoreTransactionJournal.ComputePlanSha256(expected),
            RestoreTransactionJournal.ComputePlanSha256(actual));
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
        string root = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-stage8-integrated-recovery-tests",
            Guid.NewGuid().ToString("N"));
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

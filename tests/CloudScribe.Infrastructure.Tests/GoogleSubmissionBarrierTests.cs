using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleSubmissionBarrierTests
{
    [Fact]
    public async Task AtomicClaimSurvivesRestartAndRejectsStaleOrForeignWrites()
    {
        using var fixture = new Fixture();
        await fixture.MigrateAsync();
        var store = new GoogleGenerationPersistedQueueStateStore(fixture);
        var initial = new GoogleGenerationPersistedQueueState("synthetic-account", "synthesize-speech", "synthetic-key", false, null);
        await store.SaveAsync(initial, TestContext.Current.CancellationToken);
        var first = Claim(initial);
        var second = Claim(initial);
        bool[] won = await Task.WhenAll(store.TryClaimSubmissionAsync(first, TestContext.Current.CancellationToken),
            new GoogleGenerationPersistedQueueStateStore(fixture).TryClaimSubmissionAsync(second, TestContext.Current.CancellationToken));
        Assert.Single(won, value => value);
        var owner = won[0] ? first : second;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveOutcomeAsync(owner with
        {
            UnresolvedSubmission = false,
            DiagnosticCode = "google-transport-timeout",
        }, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationPersistedQueueTransitionPolicy.ValidateTransition(owner,
            owner with { UnresolvedSubmission = false, DiagnosticCode = "google-transport-timeout" },
            GoogleGenerationReconciliationResolutionEvidence.ProviderConfirmedTerminal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(initial, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveOutcomeAsync(owner with
        {
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-transport-timeout",
        }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveOutcomeAsync(owner with
        {
            PayloadSha256 = new string('b', 64),
            DiagnosticCode = "google-transport-timeout",
        }, TestContext.Current.CancellationToken));
        await store.SaveOutcomeAsync(owner with { DiagnosticCode = "google-transport-timeout" }, TestContext.Current.CancellationToken);
        var reloaded = Assert.IsType<GoogleGenerationPersistedQueueState>(await new GoogleGenerationPersistedQueueStateStore(fixture)
            .LoadAsync(initial.AccountId, initial.OperationStableId, initial.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.True(reloaded.UnresolvedSubmission);
        Assert.Null(reloaded.ProviderRequestId);
        Assert.True(reloaded.SubmissionAttempted);
        Assert.Equal(owner.LocalAttemptId, reloaded.LocalAttemptId);
        Assert.Equal("google-transport-timeout", reloaded.DiagnosticCode);
        Assert.False(await store.TryClaimSubmissionAsync(second, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationPersistedQueueStatePolicy.RequireCompatible(reloaded,
            initial.AccountId, initial.OperationStableId, initial.IdempotencyKey));
    }

    [Fact]
    public async Task AcceptedWithoutProviderIdCannotReplayAndDoesNotInventIdentity()
    {
        using var fixture = new Fixture();
        await fixture.MigrateAsync();
        var store = new GoogleGenerationPersistedQueueStateStore(fixture);
        var initial = new GoogleGenerationPersistedQueueState("synthetic-account", "synthesize-speech", "accepted-key", false, null);
        await store.SaveAsync(initial, TestContext.Current.CancellationToken);
        var claim = Claim(initial);
        Assert.True(await store.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        await store.SaveOutcomeAsync(claim with { UnresolvedSubmission = false, DiagnosticCode = "google-accepted" }, TestContext.Current.CancellationToken);
        var accepted = Assert.IsType<GoogleGenerationPersistedQueueState>(await store.LoadAsync(initial.AccountId,
            initial.OperationStableId, initial.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.False(accepted.UnresolvedSubmission);
        Assert.Null(accepted.ProviderRequestId);
        Assert.True(accepted.SubmissionAttempted);
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationPersistedQueueStatePolicy.RequireCompatible(accepted,
            initial.AccountId, initial.OperationStableId, initial.IdempotencyKey));
        Assert.False(await store.TryClaimSubmissionAsync(Claim(initial), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MigrationPreservesLegacyEvidenceAndBlocksEveryLegacyRow()
    {
        using var fixture = new Fixture();
        await using (var context = fixture.CreateDbContext())
        {
            string previous = context.Database.GetMigrations().Last(id => string.CompareOrdinal(id, "20261027010000_GoogleSubmissionAttemptBarrier") < 0);
            await context.GetService<IMigrator>().MigrateAsync(previous, TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO google_generation_queue_states VALUES ('a','s','legacy-no-id',0,NULL),('a','s','legacy-id',1,'retained-real-id');", TestContext.Current.CancellationToken);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        var store = new GoogleGenerationPersistedQueueStateStore(fixture);
        var noId = Assert.IsType<GoogleGenerationPersistedQueueState>(await store.LoadAsync("a", "s", "legacy-no-id", TestContext.Current.CancellationToken));
        Assert.True(noId.SubmissionAttempted);
        Assert.False(noId.UnresolvedSubmission);
        Assert.Null(noId.ProviderRequestId);
        var known = Assert.IsType<GoogleGenerationPersistedQueueState>(await store.LoadAsync("a", "s", "legacy-id", TestContext.Current.CancellationToken));
        Assert.True(known.UnresolvedSubmission);
        Assert.Equal("retained-real-id", known.ProviderRequestId);
        Assert.Null(known.LocalAttemptId);
        Assert.Null(known.DiagnosticCode);
        Assert.False(await store.TryClaimSubmissionAsync(Claim(noId), TestContext.Current.CancellationToken));
    }

    private static GoogleGenerationPersistedQueueState Claim(GoogleGenerationPersistedQueueState initial) => initial with
    {
        SubmissionAttempted = true,
        UnresolvedSubmission = true,
        LocalAttemptId = Guid.NewGuid().ToString("N"),
        DiagnosticCode = "google-dispatch-claimed",
        PayloadSha256 = new string('a', 64),
        PayloadBytes = 42,
        OutcomeObservedAtUnixMilliseconds = 1,
    };

    private sealed class Fixture : IDbContextFactory<CloudScribeDbContext>, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudscribe-barrier-tests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(_root);
        public CloudScribeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CloudScribeDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "synthetic.db"), Pooling = false }.ConnectionString).Options);
        public async Task MigrateAsync()
        {
            using var context = CreateDbContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

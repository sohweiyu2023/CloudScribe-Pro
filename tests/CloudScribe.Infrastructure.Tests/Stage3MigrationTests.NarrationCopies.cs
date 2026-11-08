using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class Stage3MigrationTests
{
    [Fact]
    public async Task NarrationCopyMigrationPreservesEveryPriorBusinessRowIncludingSpendAndQueueAuthority()
    {
        string root = CreateTemporaryRoot(), file = Path.Combine(root, "pre-narration-copy.db");
        try
        {
            using var db = CreateContext(file);
            await db.GetService<IMigrator>().MigrateAsync(ChirpBoundedSentenceRecovery.MigrationId, TestContext.Current.CancellationToken);
            Guid documentId = Guid.NewGuid();
            Guid revisionId = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO documents (Id,Title,DraftText,CreatedAtUnixMilliseconds,UpdatedAtUnixMilliseconds,Status,IsFavorite,CurrentRevisionId,ConcurrencyVersion)
                VALUES ({0},'Synthetic retained document','Exact https://example.test source',1,1,0,0,{1},1);
                INSERT INTO document_revisions (Id,DocumentId,CreatedAtUnixMilliseconds,RevisionKind,ContentText,ContentSha256)
                VALUES ({1},{0},1,1,'Exact https://example.test source',{2});
                """, [documentId, revisionId, new string('b', 64)], TestContext.Current.CancellationToken);
            var factory = new ChirpMigrationFactory(file);
            var queue = new GoogleGenerationPersistedQueueStateStore(factory);
            var state = new GoogleGenerationPersistedQueueState("synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-old-job", false, null);
            await queue.SaveAsync(state, TestContext.Current.CancellationToken);
            Assert.True(await queue.TryClaimSubmissionAsync(state with
            {
                SubmissionAttempted = true,
                UnresolvedSubmission = true,
                LocalAttemptId = Guid.NewGuid().ToString("N"),
                DiagnosticCode = "google-dispatch-claimed",
                PayloadSha256 = new string('a', 64),
                PayloadBytes = 100,
                OutcomeObservedAtUnixMilliseconds = 1,
            }, TestContext.Current.CancellationToken));
            var envelope = new GoogleGenerationSubmissionEnvelope(state.AccountId, "synthetic-reference", "synthetic-capability", "synthetic-pricing",
                1, "synthetic-voice", "MP3", new string('a', 64), 100);
            await new GoogleGenerationSpendAuthorizationStore(factory, TimeProvider.System).SaveApprovedAsync(
                GoogleGenerationSpendAuthorization.Create(envelope, "USD", 2, 3, 3), TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO billable_operation_ledger (Id,OperationId,SnapshotId,EventKind,OccurredAtUnixMilliseconds,AmountUnits,AmountScale,CurrencyCode,CorrelationId,EventCode) VALUES ({0},{1},'synthetic-snapshot',1,1,123,2,'USD','synthetic-correlation','synthetic-retained');",
                [Guid.NewGuid(), Guid.NewGuid()], TestContext.Current.CancellationToken);
            var before = await ChirpBusinessRowsAsync(db);
            await db.GetService<IMigrator>().MigrateAsync(NarrationCopies.MigrationId, TestContext.Current.CancellationToken);
            var after = await ChirpBusinessRowsAsync(db);
            Assert.Equal(before.Count + 2, after.Count);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
            Assert.Equal("[]", after["batch_source_imports"]);
            Assert.Equal("[]", after["narration_copies"]);
            await Assert.ThrowsAsync<NotSupportedException>(() => db.GetService<IMigrator>().MigrateAsync(
                ChirpBoundedSentenceRecovery.MigrationId, TestContext.Current.CancellationToken));
            foreach (var (table, rows) in before) Assert.Equal(rows, (await ChirpBusinessRowsAsync(db))[table]);
        }
        finally { SqliteConnection.ClearAllPools(); DeleteTemporaryRoot(root); }
    }
}

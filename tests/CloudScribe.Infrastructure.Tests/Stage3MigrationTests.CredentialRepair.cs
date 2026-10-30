using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class Stage3MigrationTests
{
    [Fact]
    public async Task CredentialRepairMigrationPreservesEveryPriorRowAndAddsOnlyEmptyRepairJournalTables()
    {
        string root = CreateTemporaryRoot(), file = Path.Combine(root, "pre-credential-repair.db");
        try
        {
            using var db = CreateContext(file);
            await db.GetService<IMigrator>().MigrateAsync(ChirpLegacyAssociations.MigrationId, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO documents(Id,Title,DraftText,CreatedAtUnixMilliseconds,UpdatedAtUnixMilliseconds,Status,IsFavorite,CurrentRevisionId,ConcurrencyVersion)
                    VALUES('674ad947-9cd7-4926-b2e0-32814a3a8d92','Synthetic retained article','Keep every source word.',1,1,0,0,NULL,1);
                INSERT INTO provider_accounts VALUES('google-cloud-text-to-speech','synthetic-existing','Synthetic retained account','synthetic-existing-key',
                    'synthetic-endpoint','global',1,7,1,2,'https://texttospeech.googleapis.com/');
                INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,SubmissionAttempted)
                    VALUES('synthetic-existing','synthesize-speech','synthetic-unknown-held',1,NULL,1);
                INSERT INTO google_generation_spend_authorizations VALUES(
                    'd5b9a243-de35-450e-b1fd-6d689bac696d','synthetic-existing','synthetic-existing-key','old-capability','old-pricing',
                    1,'cmn-CN-Chirp3-HD-Alnilam','MP3','synthetic-held-payload',1,'USD',2,3,3,1);
                """, TestContext.Current.CancellationToken);
            var before = await ChirpBusinessRowsAsync(db);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var after = await ChirpBusinessRowsAsync(db);
            Assert.Equal(before.Count + 2, after.Count);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
            Assert.Equal("[]", after["google_credential_repairs"]);
            Assert.Equal("[]", after["google_credential_repair_events"]);
            await Assert.ThrowsAsync<NotSupportedException>(() => db.GetService<IMigrator>()
                .MigrateAsync(ChirpLegacyAssociations.MigrationId, TestContext.Current.CancellationToken));
            foreach (var (table, rows) in before) Assert.Equal(rows, (await ChirpBusinessRowsAsync(db))[table]);
        }
        finally { SqliteConnection.ClearAllPools(); DeleteTemporaryRoot(root); }
    }
}

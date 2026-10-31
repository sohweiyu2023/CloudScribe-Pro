using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class Stage3MigrationTests
{
    [Fact]
    public async Task SuccessorMigrationPreservesOldOwnersReceiptsJournalsAndPhysicalQueueSchema()
    {
        string root = CreateTemporaryRoot(), file = Path.Combine(root, "pre-successor.db");
        try
        {
            using var db = CreateContext(file);
            await db.GetService<IMigrator>().MigrateAsync(GoogleCredentialRepairs.MigrationId, TestContext.Current.CancellationToken);
            await SeedOriginalArticleOwnersAsync(db);
            var before = await ChirpBusinessRowsAsync(db);
            await db.GetService<IMigrator>().MigrateAsync(ChirpArticleSuccessors.MigrationId, TestContext.Current.CancellationToken);
            var after = await ChirpBusinessRowsAsync(db);
            Assert.Equal(before.Count + 2, after.Count);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
            Assert.Equal("[]", after["chirp_article_successor_attempts"]);
            Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM chirp_article_authorizations").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM chirp_article_owned_attempts").SingleAsync(TestContext.Current.CancellationToken));
            Assert.True(await db.Database.SqlQueryRaw<long>("SELECT cid AS Value FROM pragma_table_info('google_generation_queue_states') WHERE name='ErrorStatus'").SingleAsync(TestContext.Current.CancellationToken)
                < await db.Database.SqlQueryRaw<long>("SELECT cid AS Value FROM pragma_table_info('google_generation_queue_states') WHERE name='SubmissionAttempted'").SingleAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM chirp_article_attempts", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(() => db.GetService<IMigrator>().MigrateAsync(GoogleCredentialRepairs.MigrationId, TestContext.Current.CancellationToken));
            foreach (var (table, rows) in before) Assert.Equal(rows, (await ChirpBusinessRowsAsync(db))[table]);
        }
        finally { SqliteConnection.ClearAllPools(); DeleteTemporaryRoot(root); }
    }

    private static async Task SeedOriginalArticleOwnersAsync(CloudScribeDbContext db)
    {
        // Intentionally non-GUID insertion order: prior digest ordering must remain stable.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO chirp_article_plans VALUES('synthetic-plan','synthetic-document','synthetic-revision','synthetic-account','synthetic-project','{{}}','{{}}','[]');
            INSERT INTO chirp_article_attempts VALUES('ffffffffffffffffffffffffffffffff','synthetic-plan',1,'old-policy','old-approval','synthetic-account','synthetic-project',
                'synthetic-failed','old-review1','{{}}','USD',2,1,1,'old-financial',1,1,'fffffffffffffffffffffffffffffffe');
            INSERT INTO chirp_article_attempts VALUES('11111111111111111111111111111111','synthetic-plan',2,'old-policy','old-approval','synthetic-account','synthetic-project',
                'synthetic-accepted','old-review2','{{}}','USD',2,1,1,'old-financial',1,1,'11111111111111111111111111111112');
            INSERT INTO chirp_article_receipts VALUES('synthetic-plan',2,'11111111111111111111111111111111','{{"synthetic":"retained receipt"}}','retained-hash');
            INSERT INTO chirp_article_output_journals VALUES('11111111111111111111111111111111','{{"synthetic":"retained receipt"}}','retained-hash');
            """, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}

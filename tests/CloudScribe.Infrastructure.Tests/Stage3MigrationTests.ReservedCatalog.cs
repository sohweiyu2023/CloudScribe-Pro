using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class Stage3MigrationTests
{
    [Fact]
    public async Task ReservedCatalogMigrationAddsOnlyEmptyAuditTablesAndPreservesEveryPriorBusinessRow()
    {
        string root = CreateTemporaryRoot(), file = Path.Combine(root, "reserved-catalog-upgrade.db");
        try
        {
            using var db = CreateContext(file); var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(GoogleCredentialRepairs.MigrationId, TestContext.Current.CancellationToken);
            await SeedOriginalArticleOwnersAsync(db);
            await migrator.MigrateAsync(ChirpArticleSuccessors.MigrationId, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO chirp_article_attempts VALUES('22222222222222222222222222222222','synthetic-plan',3,'old-policy','old-approval',
                    'synthetic-account','synthetic-project','synthetic-held','old-held-review','{{}}','USD',2,1,1,'held-financial',1,0,NULL);
                """, TestContext.Current.CancellationToken);
            var before = await ChirpBusinessRowsAsync(db);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var after = await ChirpBusinessRowsAsync(db);
            Assert.Equal(before.Count + 2, after.Count);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
            Assert.Equal("[]", after["chirp_reserved_catalog_mappings"]);
            Assert.Equal("[]", after["chirp_reserved_dispatch_mappings"]);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts WHERE Consumed=0 AND ReservedMinorUnits=1").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check").ToArrayAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(ChirpArticleSuccessors.MigrationId, TestContext.Current.CancellationToken));
            foreach (var (table, rows) in before) Assert.Equal(rows, (await ChirpBusinessRowsAsync(db))[table]);
        }
        finally { SqliteConnection.ClearAllPools(); DeleteTemporaryRoot(root); }
    }
}

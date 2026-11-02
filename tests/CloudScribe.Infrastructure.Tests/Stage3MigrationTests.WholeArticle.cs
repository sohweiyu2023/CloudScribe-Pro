using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class Stage3MigrationTests
{
    [Fact]
    public async Task WholeArticleMigrationPreservesEveryPriorBusinessRowAndRetainedHold()
    {
        string root = CreateTemporaryRoot(), file = Path.Combine(root, "pre-whole-article.db");
        try
        {
            using var db = CreateContext(file);
            await db.GetService<IMigrator>().MigrateAsync(ChirpReservationCatalogMappings.MigrationId, TestContext.Current.CancellationToken);
            var envelope = new GoogleGenerationSubmissionEnvelope("synthetic-account", "synthetic-reference", "synthetic-capability", "synthetic-price", 1,
                "cmn-CN-Chirp3-HD-Alnilam", "MP3", new string('a', 64), 100);
            await new GoogleGenerationSpendAuthorizationStore(new ChirpMigrationFactory(file), TimeProvider.System).SaveApprovedAsync(
                GoogleGenerationSpendAuthorization.Create(envelope, "USD", 2, 3, 3), TestContext.Current.CancellationToken);
            var before = await ChirpBusinessRowsAsync(db);
            await db.GetService<IMigrator>().MigrateAsync(ChirpWholeArticleJobs.MigrationId, TestContext.Current.CancellationToken);
            var after = await ChirpBusinessRowsAsync(db);
            Assert.Equal(before.Count + 2, after.Count);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
            Assert.Equal("[]", after["chirp_whole_article_jobs"]);
            Assert.Equal("[]", after["chirp_whole_article_events"]);
            await Assert.ThrowsAsync<NotSupportedException>(() => db.GetService<IMigrator>().MigrateAsync(ChirpReservationCatalogMappings.MigrationId, TestContext.Current.CancellationToken));
            foreach (var (table, rows) in before) Assert.Equal(rows, (await ChirpBusinessRowsAsync(db))[table]);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); DeleteTemporaryRoot(root); }
    }
}

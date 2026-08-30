using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage3MigrationTests
{
    [Fact]
    public async Task FreshDatabaseAppliesExecutableStage2ThroughStage6Migrations()
    {
        string root = CreateTemporaryRoot();
        string databasePath = Path.Combine(root, "fresh.db");
        try
        {
            await using CloudScribeDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            string[] migrations = (await context.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .ToArray();
            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                    Stage4PricingContractOverrides.MigrationId,
                    Stage4ProviderAccountsAndCapabilities.MigrationId,
                    Stage6ProviderEndpointOrigin.MigrationId,
                ],
                migrations);

            Guid documentId = Guid.NewGuid();
            Guid revisionId = Guid.NewGuid();
            context.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                Title = "Stage 3",
                DraftText = "durable draft",
                CreatedAtUnixMilliseconds = 1,
                UpdatedAtUnixMilliseconds = 1,
                Status = 0,
                CurrentRevisionId = revisionId,
                ConcurrencyVersion = 1,
            });
            context.DocumentRevisions.Add(new DocumentRevisionEntity
            {
                Id = revisionId,
                DocumentId = documentId,
                CreatedAtUnixMilliseconds = 1,
                RevisionKind = 0,
                Name = "checkpoint",
                ContentText = "durable draft",
                ContentSha256 = new string('a', 64),
                ContentRelativePath = "documents/example/content/revision.utf8",
                ContentByteLength = 13,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal("durable draft", (await context.Documents.SingleAsync(TestContext.Current.CancellationToken)).DraftText);
            DocumentRevisionEntity revision = await context.DocumentRevisions.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("documents/example/content/revision.utf8", revision.ContentRelativePath);
            Assert.Equal(13, revision.ContentByteLength);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Slice1DatabaseUpgradesWithoutLosingExistingDocumentRows()
    {
        string root = CreateTemporaryRoot();
        string databasePath = Path.Combine(root, "slice1-upgrade.db");
        Guid documentId = Guid.NewGuid();
        Guid revisionId = Guid.NewGuid();
        try
        {
            await using (CloudScribeDbContext slice1 = CreateContext(databasePath))
            {
                IMigrator migrator = slice1.GetService<IMigrator>();
                await migrator.MigrateAsync(Stage3Documents.MigrationId, TestContext.Current.CancellationToken);
                await slice1.Database.ExecuteSqlRawAsync(
                    "INSERT INTO documents (Id, Title, DraftText, CreatedAtUnixMilliseconds, UpdatedAtUnixMilliseconds, Status, IsFavorite, CurrentRevisionId, VoiceReference, PresetReference, ConcurrencyVersion) " +
                    "VALUES ({0}, {1}, {2}, 1, 1, 0, 0, {3}, NULL, NULL, 1);",
                    [documentId, "Preserve", "slice one text", revisionId],
                    TestContext.Current.CancellationToken);
                await slice1.Database.ExecuteSqlRawAsync(
                    "INSERT INTO document_revisions (Id, DocumentId, CreatedAtUnixMilliseconds, RevisionKind, Name, ContentText, ContentSha256, ImportProvenance) " +
                    "VALUES ({0}, {1}, 1, 0, NULL, {2}, {3}, NULL);",
                    [revisionId, documentId, "slice one text", new string('b', 64)],
                    TestContext.Current.CancellationToken);
            }

            await using CloudScribeDbContext upgraded = CreateContext(databasePath);
            await upgraded.Database.MigrateAsync(TestContext.Current.CancellationToken);

            string[] migrations = (await upgraded.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .ToArray();
            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                    Stage4PricingContractOverrides.MigrationId,
                    Stage4ProviderAccountsAndCapabilities.MigrationId,
                    Stage6ProviderEndpointOrigin.MigrationId,
                ],
                migrations);
            DocumentEntity document = await upgraded.Documents.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(documentId, document.Id);
            Assert.Equal("slice one text", document.DraftText);
            DocumentRevisionEntity revision = await upgraded.DocumentRevisions.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Null(revision.ContentRelativePath);
            Assert.Null(revision.ContentByteLength);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Stage2EnsureCreatedShapeIsBridgedWithoutDroppingExistingRows()
    {
        string root = CreateTemporaryRoot();
        string databasePath = Path.Combine(root, "legacy.db");
        Guid activityId = Guid.NewGuid();
        try
        {
            await using (CloudScribeDbContext baseline = CreateContext(databasePath))
            {
                IMigrator migrator = baseline.GetService<IMigrator>();
                await migrator.MigrateAsync(Stage2Baseline.MigrationId, TestContext.Current.CancellationToken);
                baseline.ActivityTimeline.Add(new ActivityTimelineEntity
                {
                    Id = activityId,
                    OccurredAtUnixMilliseconds = 123,
                    Severity = 0,
                    EventCode = "LEGACY",
                    Summary = "Preserve me",
                    CorrelationId = "migration-test",
                });
                await baseline.SaveChangesAsync(TestContext.Current.CancellationToken);
                await baseline.Database.ExecuteSqlRawAsync(
                    "DROP TABLE \"__EFMigrationsHistory\";",
                    TestContext.Current.CancellationToken);
            }

            await using CloudScribeDbContext upgrade = CreateContext(databasePath);
            SqliteConnection connection = (SqliteConnection)upgrade.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            LegacyDatabaseMigrationBridge bridge = new();
            await bridge.PrepareAsync(connection, TestContext.Current.CancellationToken);
            await upgrade.Database.MigrateAsync(TestContext.Current.CancellationToken);

            string[] migrations = (await upgrade.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .ToArray();
            Assert.Equal(
                [
                    Stage2Baseline.MigrationId,
                    Stage3Documents.MigrationId,
                    Stage3DocumentWorkflow.MigrationId,
                    Stage4PricingCatalogHistory.MigrationId,
                    Stage4PricingContractOverrides.MigrationId,
                    Stage4ProviderAccountsAndCapabilities.MigrationId,
                    Stage6ProviderEndpointOrigin.MigrationId,
                ],
                migrations);
            ActivityTimelineEntity preserved = await upgrade.ActivityTimeline.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(activityId, preserved.Id);
            Assert.Equal("Preserve me", preserved.Summary);
            Assert.Empty(await upgrade.Documents.ToListAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PartialLegacySchemaFailsClosedInsteadOfGuessing()
    {
        string root = CreateTemporaryRoot();
        string databasePath = Path.Combine(root, "partial.db");
        try
        {
            await using SqliteConnection connection = new($"Data Source={databasePath}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE activity_timeline (Id TEXT PRIMARY KEY);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            LegacyDatabaseMigrationBridge bridge = new();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                bridge.PrepareAsync(connection, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task AbandonedEfMigrationLockIsClearedOnlyAfterObtainingAWriteLock()
    {
        string root = CreateTemporaryRoot();
        string databasePath = Path.Combine(root, "lock.db");
        try
        {
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                DefaultTimeout = 1,
            }.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (SqliteCommand seed = connection.CreateCommand())
            {
                seed.CommandText =
                    "CREATE TABLE \"__EFMigrationsLock\" (Id INTEGER NOT NULL PRIMARY KEY); " +
                    "INSERT INTO \"__EFMigrationsLock\" (Id) VALUES (1);";
                await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            LegacyDatabaseMigrationBridge bridge = new();
            await LegacyDatabaseMigrationBridge.RecoverAbandonedEfMigrationLockAsync(connection, TestContext.Current.CancellationToken);

            await using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task UnknownLegacyTableFailsClosedInsteadOfClaimingStage2Ownership()
    {
        string root = CreateTemporaryRoot();
        string databasePath = Path.Combine(root, "unknown.db");
        try
        {
            await using SqliteConnection connection = new($"Data Source={databasePath}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE unexpected_table (Id TEXT PRIMARY KEY);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            LegacyDatabaseMigrationBridge bridge = new();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                bridge.PrepareAsync(connection, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static CloudScribeDbContext CreateContext(string databasePath)
    {
        DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true,
            }.ConnectionString)
            .Options;
        return new CloudScribeDbContext(options);
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage3-migration-tests", Guid.NewGuid().ToString("N"));
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

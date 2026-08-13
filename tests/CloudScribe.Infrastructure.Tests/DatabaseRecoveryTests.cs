using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class DatabaseRecoveryTests
{
    [Fact]
    public async Task InitializerCreatesVerifiedBackupBeforeUpgradingExistingDatabase()
    {
        string root = CreateTemporaryRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            paths.EnsureDatabaseDirectory();
            Guid activityId = Guid.NewGuid();
            await using (CloudScribeDbContext seed = CreateContext(paths.DatabasePath))
            {
                IMigrator migrator = seed.GetService<IMigrator>();
                await migrator.MigrateAsync(Stage3Documents.MigrationId, TestContext.Current.CancellationToken);
                seed.ActivityTimeline.Add(new ActivityTimelineEntity
                {
                    Id = activityId,
                    OccurredAtUnixMilliseconds = 123,
                    Severity = 0,
                    EventCode = "BACKUP",
                    Summary = "Preserve before migration",
                    CorrelationId = "stage3-recovery-test",
                });
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            DatabaseInitializer initializer = CreateInitializer(paths);
            await initializer.InitializeAsync(TestContext.Current.CancellationToken);

            string backupPath = Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "pre-migration-*.db"));
            await using CloudScribeDbContext backup = CreateContext(backupPath);
            string[] backupMigrations = (await backup.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .ToArray();
            Assert.Equal([Stage2Baseline.MigrationId, Stage3Documents.MigrationId], backupMigrations);
            ActivityTimelineEntity preserved = await backup.ActivityTimeline.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(activityId, preserved.Id);
            Assert.Equal("Preserve before migration", preserved.Summary);

            await using CloudScribeDbContext current = CreateContext(paths.DatabasePath);
            Assert.Equal(3, (await current.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task FailedMigrationRestoresVerifiedPreMigrationDatabase()
    {
        string root = CreateTemporaryRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            Guid documentId = await SeedMigrationFailureDatabaseAsync(paths);

            DatabaseInitializer initializer = CreateInitializer(paths);
            await Assert.ThrowsAsync<SqliteException>(() =>
                initializer.InitializeAsync(TestContext.Current.CancellationToken));

            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "pre-migration-*.db"));
            await AssertRestoredMigrationFailureDatabaseAsync(paths, documentId);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CorruptDatabaseFailsClosedWithoutReplacingOriginalBytes()
    {
        string root = CreateTemporaryRoot();
        try
        {
            AppPaths paths = CreatePaths(root);
            paths.EnsureDatabaseDirectory();
            byte[] corrupt = [0x43, 0x6C, 0x6F, 0x75, 0x64, 0x53, 0x63, 0x72, 0x69, 0x62, 0x65];
            await File.WriteAllBytesAsync(paths.DatabasePath, corrupt, TestContext.Current.CancellationToken);

            DatabaseInitializer initializer = CreateInitializer(paths);
            await Assert.ThrowsAsync<SqliteException>(() =>
                initializer.InitializeAsync(TestContext.Current.CancellationToken));

            Assert.Equal(corrupt, await File.ReadAllBytesAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "pre-migration-*.db"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static async Task<Guid> SeedMigrationFailureDatabaseAsync(AppPaths paths)
    {
        paths.EnsureDatabaseDirectory();
        Guid documentId = Guid.NewGuid();
        Guid revisionId = Guid.NewGuid();
        await using CloudScribeDbContext seed = CreateContext(paths.DatabasePath);
        IMigrator migrator = seed.GetService<IMigrator>();
        await migrator.MigrateAsync(Stage3Documents.MigrationId, TestContext.Current.CancellationToken);
        await seed.Database.ExecuteSqlRawAsync(
            "ALTER TABLE document_revisions ADD COLUMN ContentRelativePath TEXT NULL;",
            TestContext.Current.CancellationToken);
        seed.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            Title = "Recovery sentinel",
            DraftText = "must survive",
            CreatedAtUnixMilliseconds = 1,
            UpdatedAtUnixMilliseconds = 1,
            Status = 0,
            CurrentRevisionId = revisionId,
            ConcurrencyVersion = 1,
        });
        seed.DocumentRevisions.Add(new DocumentRevisionEntity
        {
            Id = revisionId,
            DocumentId = documentId,
            CreatedAtUnixMilliseconds = 1,
            RevisionKind = 0,
            Name = "before failure",
            ContentText = "must survive",
            ContentSha256 = new string('c', 64),
        });
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        return documentId;
    }

    private static async Task AssertRestoredMigrationFailureDatabaseAsync(AppPaths paths, Guid documentId)
    {
        await using CloudScribeDbContext restored = CreateContext(paths.DatabasePath);
        string[] migrations = (await restored.Database
            .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
            .ToArray();
        Assert.Equal([Stage2Baseline.MigrationId, Stage3Documents.MigrationId], migrations);
        DocumentEntity document = await restored.Documents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(documentId, document.Id);
        Assert.Equal("must survive", document.DraftText);

        await using SqliteConnection connection = new($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(document_revisions);";
        await using SqliteDataReader reader = await columns.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        List<string> names = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(1));
        }

        Assert.Contains("ContentRelativePath", names, StringComparer.Ordinal);
        Assert.DoesNotContain("ContentByteLength", names, StringComparer.Ordinal);
    }

    private static DatabaseInitializer CreateInitializer(AppPaths paths) => new(
        paths,
        new TestContextFactory(paths.DatabasePath),
        new LegacyDatabaseMigrationBridge(),
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)),
        NullLogger<DatabaseInitializer>.Instance);

    private static AppPaths CreatePaths(string root) => new(Options.Create(new CloudScribeOptions
    {
        AppDataDirectoryOverride = root,
    }));

    private static CloudScribeDbContext CreateContext(string databasePath)
    {
        DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true,
                DefaultTimeout = 5,
            }.ConnectionString)
            .Options;
        return new CloudScribeDbContext(options);
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage3-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        SqliteConnection.ClearAllPools();
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

    private sealed class TestContextFactory(string databasePath) : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => CreateContext(databasePath);

        public Task<CloudScribeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateContext(databasePath));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

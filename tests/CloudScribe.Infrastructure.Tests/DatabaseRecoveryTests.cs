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
            using (CloudScribeDbContext seed = CreateContext(paths.DatabasePath))
            {
                IMigrator migrator = seed.GetService<IMigrator>();
                await migrator
                    .MigrateAsync(Stage3Documents.MigrationId, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                seed.ActivityTimeline.Add(new ActivityTimelineEntity
                {
                    Id = activityId,
                    OccurredAtUnixMilliseconds = 123,
                    Severity = 0,
                    EventCode = "BACKUP",
                    Summary = "Preserve before migration",
                    CorrelationId = "stage3-recovery-test",
                });
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            DatabaseInitializer initializer = CreateInitializer(paths);
            await initializer.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            string backupPath = Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "pre-migration-*.db"));
            using CloudScribeDbContext backup = CreateContext(backupPath);
            string[] backupMigrations = (await backup.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true))
                .ToArray();
            Assert.Equal([Stage2Baseline.MigrationId, Stage3Documents.MigrationId], backupMigrations);
            ActivityTimelineEntity preserved = await backup.ActivityTimeline
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(activityId, preserved.Id);
            Assert.Equal("Preserve before migration", preserved.Summary);

            using CloudScribeDbContext current = CreateContext(paths.DatabasePath);
            Assert.Equal(3, (await current.Database
                .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true)).Count());
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
            Guid documentId = await SeedMigrationFailureDatabaseAsync(paths).ConfigureAwait(true);

            DatabaseInitializer initializer = CreateInitializer(paths);
            await Assert.ThrowsAsync<SqliteException>(() =>
                initializer.InitializeAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);

            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "pre-migration-*.db"));
            await AssertRestoredMigrationFailureDatabaseAsync(paths, documentId).ConfigureAwait(true);
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
            await File
                .WriteAllBytesAsync(paths.DatabasePath, corrupt, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            DatabaseInitializer initializer = CreateInitializer(paths);
            await Assert.ThrowsAsync<SqliteException>(() =>
                initializer.InitializeAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);

            byte[] afterFailure = await File
                .ReadAllBytesAsync(paths.DatabasePath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(corrupt, afterFailure);
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
        using CloudScribeDbContext seed = CreateContext(paths.DatabasePath);
        IMigrator migrator = seed.GetService<IMigrator>();
        await migrator
            .MigrateAsync(Stage3Documents.MigrationId, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO documents
                (Id, Title, DraftText, CreatedAtUnixMilliseconds, UpdatedAtUnixMilliseconds,
                 Status, IsFavorite, CurrentRevisionId, VoiceReference, PresetReference, ConcurrencyVersion)
            VALUES
                ({documentId}, {"Recovery sentinel"}, {"must survive"}, {1L}, {1L},
                 {0}, {false}, {null}, {null}, {null}, {1L});
            """, TestContext.Current.CancellationToken).ConfigureAwait(true);

        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO document_revisions
                (Id, DocumentId, CreatedAtUnixMilliseconds, RevisionKind, Name,
                 ContentText, ContentSha256, ImportProvenance)
            VALUES
                ({revisionId}, {documentId}, {1L}, {0}, {"before failure"},
                 {"must survive"}, {new string('c', 64)}, {null});
            """, TestContext.Current.CancellationToken).ConfigureAwait(true);

        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE documents SET CurrentRevisionId = {revisionId} WHERE Id = {documentId};
            """, TestContext.Current.CancellationToken).ConfigureAwait(true);

        await seed.Database
            .ExecuteSqlRawAsync(
                "ALTER TABLE document_revisions ADD COLUMN ContentRelativePath TEXT NULL;",
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return documentId;
    }

    private static async Task AssertRestoredMigrationFailureDatabaseAsync(AppPaths paths, Guid documentId)
    {
        using CloudScribeDbContext restored = CreateContext(paths.DatabasePath);
        string[] migrations = (await restored.Database
            .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
            .ToArray();
        Assert.Equal([Stage2Baseline.MigrationId, Stage3Documents.MigrationId], migrations);
        DocumentEntity document = await restored.Documents
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(documentId, document.Id);
        Assert.Equal("must survive", document.DraftText);

        using SqliteConnection connection = new($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(document_revisions);";
        using SqliteDataReader reader = await columns
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        List<string> names = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
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

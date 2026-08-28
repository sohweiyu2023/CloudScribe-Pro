using CloudScribe.Application.Logging;
using CloudScribe.Application.Startup;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class DatabaseInitializer(
    AppPaths appPaths,
    IDbContextFactory<CloudScribeDbContext> contextFactory,
    LegacyDatabaseMigrationBridge legacyMigrationBridge,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger) : IApplicationInitializer
{
    private const int BusyTimeoutMilliseconds = 5000;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        appPaths.EnsureDatabaseDirectory();
        appPaths.EnsureBackupsDirectory();

        string? backupPath = await CreateVerifiedPreMigrationBackupAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyMigrationsAndVerifyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (backupPath is not null)
            {
                RestoreBackup(backupPath);
            }

            throw;
        }

        CloudScribeLog.DatabaseInitialized(logger);
    }

    private async Task ApplyMigrationsAndVerifyAsync(CancellationToken cancellationToken)
    {
        using CloudScribeDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await legacyMigrationBridge.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        await LegacyDatabaseMigrationBridge.RecoverAbandonedEfMigrationLockAsync(connection, cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        await ExecutePragmaAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await VerifyIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> CreateVerifiedPreMigrationBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(appPaths.DatabasePath) || new FileInfo(appPaths.DatabasePath).Length == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string backupPath = Path.Combine(
            appPaths.BackupsDirectory,
            $"pre-migration-{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.db");

        using (SqliteConnection source = new(BuildConnectionString(appPaths.DatabasePath, pooling: false)))
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(source, cancellationToken).ConfigureAwait(false);
            await CheckpointWalAsync(source, cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(source, cancellationToken).ConfigureAwait(false);

            using SqliteConnection destination = new(BuildConnectionString(backupPath, pooling: false));
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }

        FlushFileToDisk(backupPath);
        cancellationToken.ThrowIfCancellationRequested();
        return backupPath;
    }

    private void RestoreBackup(string backupPath)
    {
        SqliteConnection.ClearAllPools();
        string stagingPath = appPaths.DatabasePath + $".restore-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(backupPath, stagingPath, overwrite: false);
            FlushFileToDisk(stagingPath);
            File.Move(stagingPath, appPaths.DatabasePath, overwrite: true);
            TryDelete(appPaths.DatabasePath + "-wal");
            TryDelete(appPaths.DatabasePath + "-shm");
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecutePragmaAsync(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
    }

    private static async Task CheckpointWalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(FULL);";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQLite WAL checkpoint did not return a status row.");
        }

        long busy = reader.GetInt64(0);
        long logFrames = reader.GetInt64(1);
        long checkpointedFrames = reader.GetInt64(2);
        if (busy != 0 || checkpointedFrames < logFrames)
        {
            throw new IOException(
                $"SQLite WAL checkpoint was incomplete: busy={busy}, logFrames={logFrames}, checkpointedFrames={checkpointedFrames}.");
        }
    }

    private static async Task VerifyIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            object? result = await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite integrity_check failed: {result}");
            }
        }

        using SqliteCommand foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQLite foreign_key_check reported at least one violation.");
        }
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildConnectionString(string databasePath, bool pooling = true) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Cache = SqliteCacheMode.Shared,
        Pooling = pooling,
        ForeignKeys = true,
        DefaultTimeout = 5,
    }.ConnectionString;

    private static void FlushFileToDisk(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

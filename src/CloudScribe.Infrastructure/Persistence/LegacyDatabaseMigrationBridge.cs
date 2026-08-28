using CloudScribe.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class LegacyDatabaseMigrationBridge
{
    private const string EfProductVersion = "10.0.10";

    private readonly IReadOnlyDictionary<string, string[]> _stage2Columns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["activity_timeline"] =
            [
                "Id",
                "OccurredAtUnixMilliseconds",
                "Severity",
                "EventCode",
                "Summary",
                "CorrelationId",
            ],
            ["billable_operation_ledger"] =
            [
                "Id",
                "OperationId",
                "SnapshotId",
                "EventKind",
                "OccurredAtUnixMilliseconds",
                "AmountUnits",
                "AmountScale",
                "CurrencyCode",
                "CorrelationId",
                "ProviderRequestId",
                "EventCode",
            ],
        };

    public async Task PrepareAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RequireOpen(connection);

        HashSet<string> tables = await ReadUserTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        if (tables.Contains("__EFMigrationsHistory"))
        {
            return;
        }

        if (!_stage2Columns.Keys.Any(tables.Contains))
        {
            ValidateEmptyOrMigrationLockOnly(tables);
            return;
        }

        ValidateStage2TableInventory(tables);
        await ValidateStage2ColumnsAsync(connection, tables, cancellationToken).ConfigureAwait(false);
        await SeedStage2MigrationHistoryAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateEmptyOrMigrationLockOnly(HashSet<string> tables)
    {
        if (tables.Count != 0 && !tables.SetEquals(["__EFMigrationsLock"]))
        {
            throw new InvalidDataException(
                $"The existing database contains unknown tables without EF migration history: {string.Join(", ", tables.Order(StringComparer.Ordinal))}.");
        }
    }

    private void ValidateStage2TableInventory(HashSet<string> tables)
    {
        HashSet<string> allowedTables = new(_stage2Columns.Keys, StringComparer.Ordinal)
        {
            "__EFMigrationsLock",
        };
        string[] unexpectedTables = tables
            .Where(table => !allowedTables.Contains(table))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unexpectedTables.Length != 0)
        {
            throw new InvalidDataException(
                $"The existing Stage 2 database contains unexpected tables: {string.Join(", ", unexpectedTables)}. " +
                "Automatic migration was stopped instead of guessing ownership.");
        }
    }

    private async Task ValidateStage2ColumnsAsync(
        SqliteConnection connection,
        HashSet<string> tables,
        CancellationToken cancellationToken)
    {
        foreach ((string table, string[] expectedColumns) in _stage2Columns)
        {
            if (!tables.Contains(table))
            {
                throw new InvalidDataException(
                    $"The existing database contains only part of the Stage 2 schema; missing table '{table}'. " +
                    "Automatic migration was stopped to avoid misclassifying an unknown database.");
            }

            HashSet<string> actualColumns = await ReadColumnsAsync(connection, table, cancellationToken).ConfigureAwait(false);
            string[] missingColumns = expectedColumns.Where(column => !actualColumns.Contains(column)).ToArray();
            if (missingColumns.Length != 0)
            {
                throw new InvalidDataException(
                    $"The existing Stage 2 table '{table}' is missing expected columns: {string.Join(", ", missingColumns)}.");
            }
        }
    }

    private static async Task SeedStage2MigrationHistoryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand createHistory = connection.CreateCommand();
        createHistory.Transaction = transaction;
        createHistory.CommandText =
            @"CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY, ""ProductVersion"" TEXT NOT NULL);";
        await createHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using SqliteCommand seedHistory = connection.CreateCommand();
        seedHistory.Transaction = transaction;
        seedHistory.CommandText =
            @"INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"") VALUES ($migrationId, $productVersion);";
        seedHistory.Parameters.AddWithValue("$migrationId", Stage2Baseline.MigrationId);
        seedHistory.Parameters.AddWithValue("$productVersion", EfProductVersion);
        await seedHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task RecoverAbandonedEfMigrationLockAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RequireOpen(connection);

        await ExecuteSqlAsync(connection, "BEGIN IMMEDIATE;", cancellationToken).ConfigureAwait(false);
        bool committed = false;
        try
        {
            if (await TableExistsAsync(connection, "__EFMigrationsLock", cancellationToken).ConfigureAwait(false))
            {
                await ExecuteSqlAsync(connection, "DELETE FROM \"__EFMigrationsLock\";", cancellationToken).ConfigureAwait(false);
            }

            await ExecuteSqlAsync(connection, "COMMIT;", cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    await ExecuteSqlAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
                }
                catch (SqliteException)
                {
                }
            }
        }
    }

    private static void RequireOpen(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("Migration inspection requires an open SQLite connection.");
        }
    }

    private static async Task<HashSet<string>> ReadUserTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> tables = new(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", tableName);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task ExecuteSqlAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

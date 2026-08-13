from pathlib import Path
p = Path('source/src/CloudScribe.Infrastructure/Persistence/LegacyDatabaseMigrationBridge.cs')
t = p.read_text(encoding='utf-8')

field_old = '    private static readonly IReadOnlyDictionary<string, string[]> Stage2Columns ='
if t.count(field_old) != 1:
    raise SystemExit('Stage2Columns field preimage mismatch')
t = t.replace(
    field_old,
    '    private readonly IReadOnlyDictionary<string, string[]> _stage2Columns =',
    1,
)

start = t.index('    public async Task PrepareAsync')
recover = t.index('    public static async Task RecoverAbandonedEfMigrationLockAsync')
replacement = '''    public async Task PrepareAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
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

'''
t = t[:start] + replacement + t[recover:]
t = t.replace('await using SqliteCommand ', 'using SqliteCommand ')
t = t.replace('await using SqliteDataReader ', 'using SqliteDataReader ')
if '"CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"' in t or '"INSERT INTO "__EFMigrationsHistory"' in t:
    raise SystemExit('Generated C# SQL quoting is malformed')
if '@"CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory""' not in t:
    raise SystemExit('Generated C# CREATE TABLE SQL quoting contract missing')
if '@"INSERT INTO ""__EFMigrationsHistory""' not in t:
    raise SystemExit('Generated C# INSERT SQL quoting contract missing')
p.write_text(t, encoding='utf-8')
print('CLOUDSCRIBE_STAGE3_BRIDGE_ANALYZER_REPAIR=PASS')

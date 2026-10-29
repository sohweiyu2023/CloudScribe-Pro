using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

internal static class ChirpArticleSql
{
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }

    internal static async Task<IReadOnlyList<object?[]>> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken token)
    {
        if (table is not ("chirp_article_attempts" or "chirp_article_plans" or "chirp_article_receipts" or "chirp_article_output_journals" or "chirp_article_legacy_associations"))
            throw new InvalidOperationException("Unsupported article evidence table.");
        using var command = Command(connection, transaction, "SELECT * FROM " + table + " ORDER BY rowid LIMIT 10001;");
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        List<object?[]> result = [];
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (result.Count == 10_000) throw new InvalidDataException("Article evidence exceeds its reviewed row bound.");
            object[] values = new object[reader.FieldCount]; reader.GetValues(values);
            result.Add(values.Select(x => x is DBNull ? null : x).ToArray());
        }
        return result;
    }

    internal static object?[][] Extra(IReadOnlyList<object?[]> rows, IReadOnlyList<RecoveryFinancialRowPin> pins, int identityColumns)
    {
        var current = rows.ToDictionary(x => RecoveryFinancialSnapshot.Identity(x, identityColumns), StringComparer.Ordinal);
        foreach (var pin in pins)
            if (!current.Remove(pin.Identity, out var row) || !string.Equals(pin.Sha256, RecoveryFinancialSnapshot.Hash(row), StringComparison.Ordinal))
                throw new InvalidOperationException("Pinned historical financial evidence changed or disappeared.");
        return current.Values.ToArray();
    }

    internal static void Exact(IReadOnlyList<object?[]> rows, IReadOnlyList<RecoveryFinancialRowPin> pins, int columns)
    {
        if (Extra(rows, pins, columns).Length != 0) throw new InvalidOperationException("Unreviewed financial records appeared; no article request was authorized.");
    }
}

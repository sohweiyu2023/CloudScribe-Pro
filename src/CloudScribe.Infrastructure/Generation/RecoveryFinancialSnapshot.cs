using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Canonical bounded snapshot data, not an authority or a mechanism for admitting its own pins.</summary>
internal sealed record RecoveryFinancialSnapshot(IReadOnlyList<object?[]> Queue, IReadOnlyList<object?[]> Spend,
    IReadOnlyList<object?[]> Recovery, IReadOnlyList<object?[]> Scopes, IReadOnlyList<RecoveryFinancialSnapshot.QueueState> QueueStates,
    IReadOnlyList<object?[]> Transitions)
{
    public string FinancialSha256 => Hash(Queue.Concat(Spend));
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public static string Identity(object?[] row, int columns) => Hash(row.Take(columns).ToArray());

    public static async Task<RecoveryFinancialSnapshot> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken) => new(
            await ReadTableAsync(connection, transaction, "google_generation_queue_states", cancellationToken).ConfigureAwait(false),
            await ReadTableAsync(connection, transaction, "google_generation_spend_authorizations", cancellationToken).ConfigureAwait(false),
            await ReadTableAsync(connection, transaction, "google_recovery_attempts", cancellationToken).ConfigureAwait(false),
            await ReadTableAsync(connection, transaction, "google_recovery_scopes", cancellationToken).ConfigureAwait(false),
            await ReadQueuesAsync(connection, transaction, cancellationToken).ConfigureAwait(false),
            await ReadTableAsync(connection, transaction, "google_recovery_grant_transitions", cancellationToken).ConfigureAwait(false));

    internal sealed record QueueState(string Account, string Operation, string Key, long Attempted, long Unresolved,
        string? ProviderId, string? LocalAttempt, string? PayloadSha256, long? PayloadBytes, string? DiagnosticCode);

    private static async Task<IReadOnlyList<QueueState>> ReadQueuesAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT AccountId,OperationStableId,IdempotencyKey,SubmissionAttempted,UnresolvedSubmission,ProviderRequestId,LocalAttemptId,PayloadSha256,PayloadBytes,DiagnosticCode FROM google_generation_queue_states ORDER BY rowid LIMIT 10001;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<QueueState> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == 10000) throw new InvalidDataException("Recovery queue snapshot exceeds its reviewed row bound.");
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<object?[]>> ReadTableAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid LIMIT 10001;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<object?[]> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == 10000) throw new InvalidDataException("Recovery snapshot exceeds its reviewed row bound.");
            object[] values = new object[reader.FieldCount]; reader.GetValues(values);
            rows.Add(values.Select(x => x is DBNull ? null : x).ToArray());
        }
        return rows;
    }
}

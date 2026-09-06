using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationPersistedQueueStateStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory) : IGoogleGenerationPersistedQueueStateStore
{
    public async Task<GoogleGenerationPersistedQueueState?> LoadAsync(
        string accountId,
        string operationStableId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireCanonical(accountId, nameof(accountId));
        RequireCanonical(operationStableId, nameof(operationStableId));
        RequireCanonical(idempotencyKey, nameof(idempotencyKey));

        await using CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT AccountId, OperationStableId, IdempotencyKey, UnresolvedSubmission, ProviderRequestId
            FROM google_generation_queue_states
            WHERE AccountId = $accountId
              AND OperationStableId = $operationStableId
              AND IdempotencyKey = $idempotencyKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$operationStableId", operationStableId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new GoogleGenerationPersistedQueueState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetString(4)).Validate();
    }

    public async Task SaveAsync(
        GoogleGenerationPersistedQueueState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();

        await using CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO google_generation_queue_states
                (AccountId, OperationStableId, IdempotencyKey, UnresolvedSubmission, ProviderRequestId)
            VALUES
                ($accountId, $operationStableId, $idempotencyKey, $unresolvedSubmission, $providerRequestId)
            ON CONFLICT(AccountId, OperationStableId, IdempotencyKey) DO UPDATE SET
                UnresolvedSubmission = excluded.UnresolvedSubmission,
                ProviderRequestId = excluded.ProviderRequestId;
            """;
        command.Parameters.AddWithValue("$accountId", state.AccountId);
        command.Parameters.AddWithValue("$operationStableId", state.OperationStableId);
        command.Parameters.AddWithValue("$idempotencyKey", state.IdempotencyKey);
        command.Parameters.AddWithValue("$unresolvedSubmission", state.UnresolvedSubmission ? 1 : 0);
        command.Parameters.AddWithValue("$providerRequestId", (object?)state.ProviderRequestId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Google queue lookup identity '{parameterName}' must be canonical.");
        }
    }
}

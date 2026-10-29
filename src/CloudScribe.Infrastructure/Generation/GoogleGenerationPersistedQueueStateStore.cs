using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationPersistedQueueStateStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory, IChirpArticleAdmissionStore? articleAdmission = null) : IGoogleGenerationPersistedQueueStateStore
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

        using CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT AccountId, OperationStableId, IdempotencyKey, UnresolvedSubmission, ProviderRequestId,
                   SubmissionAttempted, LocalAttemptId, DiagnosticCode, PayloadSha256, PayloadBytes, OutcomeObservedAtUnixMilliseconds, ErrorStatus
            FROM google_generation_queue_states
            WHERE AccountId = $accountId
              AND OperationStableId = $operationStableId
              AND IdempotencyKey = $idempotencyKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$operationStableId", operationStableId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);

        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new GoogleGenerationPersistedQueueState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            (GoogleErrorStatus)reader.GetInt32(11)).Validate();
    }

    // Initialization is insert-only. A stale UI context cannot erase a dispatch barrier.
    public async Task SaveAsync(GoogleGenerationPersistedQueueState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IdempotencyKey.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Internal article identities must be initialized by their dedicated approval transaction.");
        state.Validate();
        if (state.SubmissionAttempted || state.UnresolvedSubmission || state.ProviderRequestId is not null
            || state.LocalAttemptId is not null || state.DiagnosticCode is not null || state.PayloadSha256 is not null
            || state.PayloadBytes is not null || state.OutcomeObservedAtUnixMilliseconds is not null || state.ErrorStatus != GoogleErrorStatus.NotProvided)
            throw new InvalidOperationException("Initialization only accepts a new never-attempted request.");
        using CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        using var initialization = connection.BeginTransaction(deferred: false);
        await ChirpLegacySpendGuard.RequireNoArticleReservationsAsync(connection, initialization, state.AccountId, cancellationToken, articleAdmission).ConfigureAwait(false);
        command.Transaction = initialization;
        command.CommandText = """
            INSERT INTO google_generation_queue_states
                (AccountId, OperationStableId, IdempotencyKey, UnresolvedSubmission, ProviderRequestId, SubmissionAttempted)
            VALUES ($accountId, $operationStableId, $idempotencyKey, 0, NULL, 0)
            ON CONFLICT(AccountId, OperationStableId, IdempotencyKey) DO NOTHING;
            """;
        AddIdentity(command, state);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); initialization.Commit();
        var current = await LoadAsync(state.AccountId, state.OperationStableId, state.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (current != state)
            throw new InvalidOperationException("Existing queue state cannot be overwritten by initialization.");
    }

    public async Task<bool> TryClaimSubmissionAsync(GoogleGenerationPersistedQueueState claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.IdempotencyKey.StartsWith(GoogleExplicitRecoveryService.KeyPrefix, StringComparison.Ordinal)
            || claim.IdempotencyKey.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("An additional attempt requires its dedicated full-identity recovery approval claim.");
        claim.Validate();
        if (!claim.SubmissionAttempted || !claim.UnresolvedSubmission || claim.ProviderRequestId is not null
            || claim.LocalAttemptId is null || !string.Equals(claim.DiagnosticCode, "google-dispatch-claimed", StringComparison.Ordinal)
            || claim.PayloadSha256 is null || claim.PayloadBytes is null)
            throw new InvalidOperationException("Dispatch requires complete local claim metadata without a fabricated provider ID.");
        using CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        using var transaction = connection.BeginTransaction(deferred: false);
        await ChirpLegacySpendGuard.RequireNoArticleReservationsAsync(connection, transaction, claim.AccountId, cancellationToken, articleAdmission).ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE google_generation_queue_states SET
                SubmissionAttempted = 1, UnresolvedSubmission = 1, LocalAttemptId = $attempt,
                DiagnosticCode = $diagnostic, PayloadSha256 = $hash, PayloadBytes = $bytes,
                OutcomeObservedAtUnixMilliseconds = $observed
            WHERE AccountId = $accountId AND OperationStableId = $operationStableId AND IdempotencyKey = $idempotencyKey
              AND SubmissionAttempted = 0 AND UnresolvedSubmission = 0 AND ProviderRequestId IS NULL AND LocalAttemptId IS NULL;
            """;
        AddMetadata(command, claim);
        bool claimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return claimed;
    }

    public async Task SaveOutcomeAsync(GoogleGenerationPersistedQueueState outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        outcome.Validate();
        if (!outcome.UnresolvedSubmission && !GoogleGenerationOutcomeDiagnostic.IsConfirmedTerminal(outcome.DiagnosticCode))
            throw new InvalidOperationException("An uncertain diagnostic cannot clear the durable reconciliation barrier.");
        if (!outcome.SubmissionAttempted || outcome.LocalAttemptId is null || outcome.DiagnosticCode is null
            || string.Equals(outcome.DiagnosticCode, "google-dispatch-claimed", StringComparison.Ordinal) || outcome.OutcomeObservedAtUnixMilliseconds is null)
            throw new InvalidOperationException("Outcome writes must retain their owned local dispatch claim.");
        using CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE google_generation_queue_states SET
                UnresolvedSubmission = $unresolved, ProviderRequestId = COALESCE($provider, ProviderRequestId),
                DiagnosticCode = $diagnostic, OutcomeObservedAtUnixMilliseconds = $observed, ErrorStatus = $errorStatus
            WHERE AccountId = $accountId AND OperationStableId = $operationStableId AND IdempotencyKey = $idempotencyKey
              AND SubmissionAttempted = 1 AND UnresolvedSubmission = 1 AND LocalAttemptId = $attempt
              AND DiagnosticCode = 'google-dispatch-claimed' AND ProviderRequestId IS NULL
              AND PayloadSha256 = $hash AND PayloadBytes = $bytes;
            """;
        AddMetadata(command, outcome);
        command.Parameters.AddWithValue("$errorStatus", (int)outcome.ErrorStatus);
        command.Parameters.AddWithValue("$unresolved", outcome.UnresolvedSubmission ? 1 : 0);
        command.Parameters.AddWithValue("$provider", (object?)outcome.ProviderRequestId ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Outcome does not own a current unresolved dispatch claim; state retained.");
    }

    private static void AddIdentity(SqliteCommand command, GoogleGenerationPersistedQueueState state)
    {
        command.Parameters.AddWithValue("$accountId", state.AccountId);
        command.Parameters.AddWithValue("$operationStableId", state.OperationStableId);
        command.Parameters.AddWithValue("$idempotencyKey", state.IdempotencyKey);
    }

    private static void AddMetadata(SqliteCommand command, GoogleGenerationPersistedQueueState state)
    {
        AddIdentity(command, state);
        command.Parameters.AddWithValue("$attempt", (object?)state.LocalAttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("$diagnostic", (object?)state.DiagnosticCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)state.PayloadSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", (object?)state.PayloadBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed", (object?)state.OutcomeObservedAtUnixMilliseconds ?? DBNull.Value);
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

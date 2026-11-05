using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleGenerationPersistedQueueStateStore
{
    private static async Task SaveSentenceHintAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleGenerationPersistedQueueState outcome, CancellationToken token)
    {
        if (outcome.ValidationHint != GoogleValidationMessageHint.SentenceLengthReported) return;
        if (outcome.UnresolvedSubmission || outcome.ProviderRequestId is not null
            || outcome.ErrorStatus != GoogleErrorStatus.InvalidArgument
            || !string.Equals(outcome.DiagnosticCode, "google-http-400", StringComparison.Ordinal))
            throw new InvalidOperationException("Only an actual terminal sentence-length response can establish native split evidence.");
        using var command = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO google_generation_sentence_rejections
                SELECT AccountId,OperationStableId,IdempotencyKey,LocalAttemptId,PayloadSha256,PayloadBytes,ErrorStatus,$hint,OutcomeObservedAtUnixMilliseconds
                FROM google_generation_queue_states WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$key
                AND SubmissionAttempted=1 AND UnresolvedSubmission=0 AND ProviderRequestId IS NULL
                AND LocalAttemptId=$attempt AND PayloadSha256=$payload AND PayloadBytes=$bytes
                AND DiagnosticCode='google-http-400' AND ErrorStatus=$status;
            """, ("$hint", (int)outcome.ValidationHint), ("$account", outcome.AccountId), ("$operation", outcome.OperationStableId),
            ("$key", outcome.IdempotencyKey), ("$attempt", outcome.LocalAttemptId), ("$payload", outcome.PayloadSha256),
            ("$bytes", outcome.PayloadBytes), ("$status", (int)GoogleErrorStatus.InvalidArgument));
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Native sentence evidence did not own the exact terminal queue outcome.");
    }

    private static async Task<GoogleValidationMessageHint> ReadSentenceHintAsync(SqliteConnection connection,
        GoogleGenerationPersistedQueueState state, CancellationToken token)
    {
        if (state.ErrorStatus != GoogleErrorStatus.InvalidArgument || state.UnresolvedSubmission
            || !string.Equals(state.DiagnosticCode, "google-http-400", StringComparison.Ordinal))
            return GoogleValidationMessageHint.NotProvided;
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='google_generation_sentence_rejections';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
            return GoogleValidationMessageHint.NotProvided;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM google_generation_sentence_rejections WHERE AccountId=$account AND OperationStableId=$operation
            AND IdempotencyKey=$key AND LocalAttemptId=$attempt AND PayloadSha256=$payload AND PayloadBytes=$bytes
            AND ErrorStatus=$status AND ValidationHint=$hint AND ObservedAtUnixMilliseconds=$observed;
            """;
        foreach (var pair in new (string, object?)[] { ("$account", state.AccountId), ("$operation", state.OperationStableId),
            ("$key", state.IdempotencyKey), ("$attempt", state.LocalAttemptId), ("$payload", state.PayloadSha256),
            ("$bytes", state.PayloadBytes), ("$status", (int)GoogleErrorStatus.InvalidArgument),
            ("$hint", (int)GoogleValidationMessageHint.SentenceLengthReported), ("$observed", state.OutcomeObservedAtUnixMilliseconds) })
            command.Parameters.AddWithValue(pair.Item1, pair.Item2 ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1
            ? GoogleValidationMessageHint.SentenceLengthReported : GoogleValidationMessageHint.NotProvided;
    }
}

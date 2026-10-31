using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<bool> WriteOwnedClaimAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, GoogleGenerationPersistedQueueState claim, CancellationToken token)
    {
        string table = await AttemptTableAsync(connection, transaction, id.ToString("N"), token).ConfigureAwait(false);
        using (var consume = ChirpArticleSql.Command(connection, transaction,
            "UPDATE " + table + " SET Consumed=1,ClaimedLocalAttemptId=$attempt WHERE AuthorizationId=$id AND Consumed=0;",
            ("$id", id.ToString("N")), ("$attempt", claim.LocalAttemptId)))
            if (await consume.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
        using (var queue = ChirpArticleSql.Command(connection, transaction, """
            UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=1,LocalAttemptId=$attempt,
            DiagnosticCode='google-dispatch-claimed',PayloadSha256=$payload,PayloadBytes=$bytes,OutcomeObservedAtUnixMilliseconds=$time
            WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$key
            AND SubmissionAttempted=0 AND UnresolvedSubmission=0 AND ProviderRequestId IS NULL AND LocalAttemptId IS NULL;
            """, ("$attempt", claim.LocalAttemptId), ("$payload", claim.PayloadSha256), ("$bytes", claim.PayloadBytes),
            ("$time", claim.OutcomeObservedAtUnixMilliseconds), ("$account", claim.AccountId), ("$operation", claim.OperationStableId), ("$key", claim.IdempotencyKey)))
            if (await queue.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
        return true;
    }
}

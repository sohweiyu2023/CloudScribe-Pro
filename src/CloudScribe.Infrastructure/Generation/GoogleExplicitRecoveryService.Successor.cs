using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    private static async Task RequireSuccessorAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryPredecessorEvidence e, bool creating, CancellationToken cancellationToken)
    {
        var s = e.Successor ?? throw new InvalidOperationException("Successor evidence is missing.");
        if (!string.Equals(Digest(e with { AdmissionSha256 = "" }), e.AdmissionSha256, StringComparison.Ordinal) || e.MaximumNewAttempts != 1 || e.MaximumAdditionalReservationMinorUnits != 3
            || s.PriorAuthorizationId == Guid.Empty || string.Equals(s.PriorAdmissionSha256, e.AdmissionSha256, StringComparison.Ordinal) || !IsDigest(s.SourceApprovalSha256) || !string.Equals(s.SourceApprovalSha256, s.SourceApprovalSha256.ToLowerInvariant(), StringComparison.Ordinal) || !IsDigest(s.FinancialStateSha256) || !IsDigest(s.PriorStateSha256))
            throw new InvalidOperationException("A distinct exact bounded successor approval is required.");
        string nonce = await RequirePriorAssociationAsync(connection, transaction, e, s, cancellationToken).ConfigureAwait(false);
        await RequirePriorExhaustedAsync(connection, transaction, s, cancellationToken).ConfigureAwait(false);
        await RequirePriorCompletedAsync(connection, transaction, e, s, nonce, cancellationToken).ConfigureAwait(false);
        await RequireSuccessorAuditAsync(connection, transaction, e, s, creating, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RequirePriorAssociationAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryPredecessorEvidence e, RecoverySuccessorGrant s, CancellationToken cancellationToken)
    {
        using (var prior = Command(connection, transaction, """
            SELECT LogicalItemId,RootHistoricalKey,RootHistoricalStateSha256,AdmissionSha256,AccountId,ProjectId,
                OperationId,DocumentId,RevisionId,TextSha256,PayloadSha256,ChildKey,Consumed,GrantMaximumNewAttempts,GrantMaximumAdditionalMinorUnits,ClaimedLocalAttemptId
            FROM google_recovery_attempts WHERE AuthorizationId=$id;
            """, ("$id", s.PriorAuthorizationId.ToString("N"))))
        using (var row = await prior.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !string.Equals(row.GetString(0), e.LogicalNarrationId, StringComparison.Ordinal) || !string.Equals(row.GetString(1), e.HistoricalKey, StringComparison.Ordinal) || !string.Equals(row.GetString(2), e.HistoricalStateSha256, StringComparison.Ordinal) || !string.Equals(row.GetString(3), s.PriorAdmissionSha256, StringComparison.Ordinal) || !string.Equals(row.GetString(4), e.AccountId, StringComparison.Ordinal) || !string.Equals(row.GetString(5), e.ProjectId, StringComparison.Ordinal) || !string.Equals(row.GetString(6), e.OperationId, StringComparison.Ordinal) || Guid.Parse(row.GetString(7)) != e.DocumentId || Guid.Parse(row.GetString(8)) != e.RevisionId
                || !string.Equals(row.GetString(9), e.TextSha256, StringComparison.Ordinal) || !string.Equals(row.GetString(10), e.OriginalPayloadSha256, StringComparison.Ordinal) || !string.Equals(row.GetString(11), s.PriorChildKey, StringComparison.Ordinal) || row.GetInt64(12) != 1 || row.IsDBNull(13) || row.IsDBNull(14) || row.IsDBNull(15))
                throw new InvalidOperationException("Successor does not preserve the exact consumed prior association.");
            return row.GetString(15);
        }
    }

    private static async Task RequirePriorExhaustedAsync(SqliteConnection connection, SqliteTransaction transaction,
        RecoverySuccessorGrant s, CancellationToken cancellationToken)
    {
        using (var capacity = Command(connection, transaction, """
            SELECT COUNT(*),SUM(ReservedMinorUnits),MIN(GrantMaximumNewAttempts),MIN(GrantMaximumAdditionalMinorUnits)
            FROM google_recovery_attempts WHERE AdmissionSha256=$grant;
            """, ("$grant", s.PriorAdmissionSha256)))
        using (var row = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || row.IsDBNull(2) || row.IsDBNull(3)
                || row.GetInt64(0) > row.GetInt64(2) || row.GetInt64(1) > row.GetInt64(3)
                || (row.GetInt64(0) < row.GetInt64(2) && row.GetInt64(1) < row.GetInt64(3)))
                throw new InvalidOperationException("Prior grant is not exhausted.");
        }
    }

    private static async Task RequirePriorCompletedAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryPredecessorEvidence e, RecoverySuccessorGrant s, string priorNonce, CancellationToken cancellationToken)
    {
        using (var state = Command(connection, transaction, """
            SELECT AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,SubmissionAttempted,
                LocalAttemptId,DiagnosticCode,PayloadSha256,PayloadBytes,OutcomeObservedAtUnixMilliseconds,ErrorStatus
            FROM google_generation_queue_states WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$child;
            """, ("$account", e.AccountId), ("$operation", e.OperationId), ("$child", s.PriorChildKey)))
        using (var row = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Prior child state is missing.");
            var q = new GoogleGenerationPersistedQueueState(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3) != 0,
                row.IsDBNull(4) ? null : row.GetString(4), row.GetInt64(5) != 0, row.IsDBNull(6) ? null : row.GetString(6),
                row.IsDBNull(7) ? null : row.GetString(7), row.IsDBNull(8) ? null : row.GetString(8), row.IsDBNull(9) ? null : row.GetInt32(9),
                row.IsDBNull(10) ? null : row.GetInt64(10), (GoogleErrorStatus)row.GetInt32(11)).Validate();
            // Narrow completed validation failures only. Network/ambiguous/accepted/in-flight outcomes cannot gain a successor.
            if (!string.Equals(Digest(q), s.PriorStateSha256, StringComparison.Ordinal) || q.UnresolvedSubmission || !q.SubmissionAttempted || q.ProviderRequestId is not null
                || !string.Equals(q.DiagnosticCode, "google-http-400", StringComparison.Ordinal) || q.ErrorStatus != GoogleErrorStatus.InvalidArgument
                || q.OutcomeObservedAtUnixMilliseconds is null || string.IsNullOrWhiteSpace(q.LocalAttemptId)
                || !string.Equals(q.PayloadSha256, e.OriginalPayloadSha256, StringComparison.Ordinal) || !string.Equals(q.LocalAttemptId, priorNonce, StringComparison.Ordinal))
                throw new InvalidOperationException("Only the exact completed failed child can receive successor authority.");
        }
    }

    private static async Task RequireSuccessorAuditAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryPredecessorEvidence e, RecoverySuccessorGrant s, bool creating, CancellationToken cancellationToken)
    {
        using var audit = Command(connection, transaction, """
            SELECT t.EvidenceJson,a.ChildKey,t.SourceApprovalSha256,t.LogicalItemId
            FROM google_recovery_grant_transitions t JOIN google_recovery_attempts a ON a.AuthorizationId=t.AuthorizationId
            WHERE t.GrantSha256=$grant;
            """, ("$grant", e.AdmissionSha256));
        string? retained = null, ownedChild = null;
        using (var row = await audit.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                retained = row.GetString(0); ownedChild = row.GetString(1);
                if (!string.Equals(row.GetString(2), s.SourceApprovalSha256, StringComparison.Ordinal) || !string.Equals(row.GetString(3), e.LogicalNarrationId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Successor approval audit was changed.");
            }
        using var head = Command(connection, transaction,
            "SELECT ChildKey FROM google_recovery_attempts WHERE LogicalItemId=$logical ORDER BY rowid DESC LIMIT 1;", ("$logical", e.LogicalNarrationId));
        string? latest = await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (creating)
        {
            using var replay = Command(connection, transaction,
                "SELECT COUNT(*) FROM google_recovery_grant_transitions WHERE SourceApprovalSha256=$source AND LogicalItemId=$logical;",
                ("$source", s.SourceApprovalSha256), ("$logical", e.LogicalNarrationId));
            if (retained is not null || !string.Equals(latest, s.PriorChildKey, StringComparison.Ordinal) || Convert.ToInt64(await replay.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0
                || !string.Equals(await FinancialDigestAsync(connection, transaction, cancellationToken).ConfigureAwait(false), s.FinancialStateSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Successor approval has already been used or financial evidence changed.");
        }
        else if (!string.Equals(retained, JsonSerializer.Serialize(e), StringComparison.Ordinal) || !string.Equals(latest, ownedChild, StringComparison.Ordinal))
            throw new InvalidOperationException("Durable successor evidence differs from admitted authority or its owned current head.");
    }


    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

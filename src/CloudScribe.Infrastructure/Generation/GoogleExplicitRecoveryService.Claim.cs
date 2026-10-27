using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    /// <summary>Use only as the owned-claim delegate of the existing production bound queue, after current admission.</summary>
    public async Task<bool> TryClaimAsync(GoogleRecoveryApprovedAttempt approval, GoogleGenerationPersistedQueueState claim,
        GoogleGenerationSubmissionEnvelope currentEnvelope, string currentProjectId, string currency, int scale, long currentEstimateMinorUnits,
        CancellationToken cancellationToken = default)
    {
        RequireClaim(approval, claim, currentEnvelope);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var find = Command(connection, transaction, """
            SELECT EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,ApprovedFinancialSha256,Consumed,DocumentId,RevisionId,TextSha256,
            LogicalItemId,RootHistoricalKey,RootHistoricalStateSha256,AdmissionSha256,GrantMaximumNewAttempts,GrantMaximumAdditionalMinorUnits
            FROM google_recovery_attempts WHERE AuthorizationId=$id AND ChildKey=$child AND ReviewSha256=$review
            AND AccountId=$account AND OperationId=$operation AND ProjectId=$project;
            """, ("$id", approval.AuthorizationId.ToString("N")), ("$child", approval.IdempotencyKey), ("$review", approval.ReviewSha256),
            ("$account", claim.AccountId), ("$operation", claim.OperationStableId), ("$project", currentProjectId));
        string financial, documentId, revisionId, textHash;
        GoogleRecoveryAssociationProof association;
        using (var row = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || row.GetInt64(6) != 0) return false;
            if (!string.Equals(row.GetString(0), JsonSerializer.Serialize(currentEnvelope), StringComparison.Ordinal) || !string.Equals(row.GetString(1), currency, StringComparison.Ordinal) || row.GetInt32(2) != scale
                || row.GetInt64(3) != currentEstimateMinorUnits || row.GetInt64(4) != approval.ReservedMinorUnits
                || currentEstimateMinorUnits > approval.ReservedMinorUnits)
                throw new InvalidOperationException("Recovery approval does not authorize this exact current envelope and estimate.");
            financial = row.GetString(5);
            documentId = row.GetString(7); revisionId = row.GetString(8); textHash = row.GetString(9);
            if (row.IsDBNull(14) || row.IsDBNull(15)) return false;
            association = new(row.GetString(10), row.GetString(11), row.GetString(12), row.GetString(13), row.GetInt32(14), row.GetInt64(15));
        }
        await RequireAssociationCurrentAsync(association, claim.AccountId, claim.OperationStableId, currentProjectId,
            Guid.Parse(documentId), Guid.Parse(revisionId), textHash, currentEnvelope.CompiledPayloadSha256, cancellationToken).ConfigureAwait(false);
        await RequireGrantCapacityAsync(connection, transaction, association, claim.AccountId, currentProjectId, 0, creating: false, cancellationToken).ConfigureAwait(false);
        using (var document = Command(connection, transaction, "SELECT CurrentRevisionId,DraftText,Status FROM documents WHERE lower(Id)=$document;", ("$document", documentId)))
        using (var row = await document.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || !string.Equals(row.GetString(0), revisionId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ArticleAudioManifest.HashText(row.GetString(1)), textHash, StringComparison.Ordinal) || row.GetInt32(2) != 0)
                throw new InvalidOperationException("Recovery document changed after approval; no dispatch is permitted.");
        if (!string.Equals(await FinancialDigestAsync(connection, transaction, cancellationToken).ConfigureAwait(false), financial, StringComparison.Ordinal))
            throw new InvalidOperationException("Financial evidence changed after recovery approval; no dispatch is permitted.");
        await RequireCurrentBudgetAsync(connection, transaction, claim.AccountId, currentProjectId, currency, scale, financial, cancellationToken).ConfigureAwait(false);
        using (var consume = Command(connection, transaction, """
            UPDATE google_recovery_attempts SET Consumed=1,ClaimedLocalAttemptId=$attempt
            WHERE AuthorizationId=$id AND ChildKey=$child AND Consumed=0;
            """, ("$attempt", claim.LocalAttemptId), ("$id", approval.AuthorizationId.ToString("N")), ("$child", claim.IdempotencyKey)))
            if (await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        using (var update = Command(connection, transaction, """
            UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=1,LocalAttemptId=$attempt,
            DiagnosticCode=$diagnostic,PayloadSha256=$payload,PayloadBytes=$bytes,OutcomeObservedAtUnixMilliseconds=$observed
            WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$child
            AND SubmissionAttempted=0 AND UnresolvedSubmission=0 AND ProviderRequestId IS NULL AND LocalAttemptId IS NULL;
            """, ("$attempt", claim.LocalAttemptId), ("$diagnostic", claim.DiagnosticCode), ("$payload", claim.PayloadSha256),
            ("$bytes", claim.PayloadBytes), ("$observed", claim.OutcomeObservedAtUnixMilliseconds),
            ("$account", claim.AccountId), ("$operation", claim.OperationStableId), ("$child", claim.IdempotencyKey)))
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    private async Task<GoogleRecoveryBudgetReview> RequireCurrentBudgetAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId,
        string currentProjectId, string currency, int scale, string financial, CancellationToken cancellationToken)
    {
        var budget = await ResolveBudgetAsync(accountId, currentProjectId, currency, scale, financial, cancellationToken).ConfigureAwait(false);
        using (var scope = Command(connection, transaction, """
            SELECT RetainedFloorMinorUnits,ProjectCeilingMinorUnits,AllocationRemainingMinorUnits FROM google_recovery_scopes
            WHERE AccountId=$account AND ProjectId=$project AND Currency=$currency AND Scale=$scale;
            """, ("$account", accountId), ("$project", currentProjectId), ("$currency", currency), ("$scale", scale)))
        using (var row = await scope.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || budget.RetainedCommittedMinorUnits < row.GetInt64(0)
                || budget.ProjectCeilingMinorUnits != row.GetInt64(1) || budget.RemainingAllocationMinorUnits > row.GetInt64(2)
                || budget.ProjectCeilingMinorUnits < budget.RetainedCommittedMinorUnits || budget.RemainingAllocationMinorUnits < 0)
                throw new InvalidOperationException("Authoritative recovery budget no longer retains this reservation.");
        return budget;
    }

    private static void RequireClaim(GoogleRecoveryApprovedAttempt approval, GoogleGenerationPersistedQueueState claim,
        GoogleGenerationSubmissionEnvelope currentEnvelope)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(claim);
        claim.Validate();
        if (currentEnvelope.RecoveryAuthorizationId != approval.AuthorizationId
            || !string.Equals(claim.IdempotencyKey, approval.IdempotencyKey, StringComparison.Ordinal) || !claim.IdempotencyKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            || !claim.SubmissionAttempted || !claim.UnresolvedSubmission || claim.ProviderRequestId is not null
            || claim.LocalAttemptId is null || !string.Equals(claim.DiagnosticCode, "google-dispatch-claimed"
, StringComparison.Ordinal) || !string.Equals(claim.PayloadSha256, currentEnvelope.CompiledPayloadSha256, StringComparison.Ordinal) || claim.PayloadBytes != currentEnvelope.CompiledPayloadBytes
            || claim.OutcomeObservedAtUnixMilliseconds is null
            || !string.Equals(claim.AccountId, currentEnvelope.AccountId, StringComparison.Ordinal) || !string.Equals(claim.OperationStableId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery dispatch requires the exact current bound local claim.");
    }

    public async Task<GoogleRecoveryApprovedAttempt?> FindApprovedAsync(string exactReviewSha256, CancellationToken cancellationToken = default)
    {
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AuthorizationId,ChildKey,ReservedMinorUnits FROM google_recovery_attempts WHERE ReviewSha256=$review;";
        command.Parameters.AddWithValue("$review", exactReviewSha256);
        using var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await row.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(Guid.ParseExact(row.GetString(0), "N"), row.GetString(1), exactReviewSha256, row.GetInt64(2)) : null;
    }
}

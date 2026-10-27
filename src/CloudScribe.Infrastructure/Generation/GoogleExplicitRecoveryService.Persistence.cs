using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    private async Task AppendApprovalAsync(SqliteConnection connection, SqliteTransaction transaction, GoogleRecoveryPreview p,
        Guid id, string child, CancellationToken cancellationToken)
    {
        var i = p.Intent;
        using (var scope = Command(connection, transaction, """
            INSERT INTO google_recovery_scopes VALUES($account,$project,$currency,$scale,$floor,$cap,$remaining)
            ON CONFLICT(AccountId,ProjectId,Currency,Scale) DO UPDATE SET
            RetainedFloorMinorUnits=$floor, AllocationRemainingMinorUnits=$remaining;
            """, ("$account", i.Envelope.AccountId), ("$project", i.ProjectId), ("$currency", i.Currency), ("$scale", i.Scale),
            ("$floor", checked(p.Budget.RetainedCommittedMinorUnits + p.AdditionalMaximumMinorUnits)),
            ("$cap", p.Budget.ProjectCeilingMinorUnits), ("$remaining", checked(p.Budget.RemainingAllocationMinorUnits - p.AdditionalMaximumMinorUnits))))
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using (var queue = Command(connection, transaction, """
            INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,SubmissionAttempted)
            VALUES($account,$operation,$child,0,NULL,0);
            """, ("$account", i.Envelope.AccountId), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$child", child)))
            await queue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        string financial = await FinancialDigestAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        using var approval = Command(connection, transaction, """
            INSERT INTO google_recovery_attempts(AuthorizationId,LogicalItemId,DocumentId,RevisionId,TextSha256,RootHistoricalKey,RootHistoricalStateSha256,AdmissionSha256,AccountId,ProjectId,OperationId,PreviousKey,
            PreviousStateSha256,ChildKey,ReviewSha256,EnvelopeJson,ApprovedFinancialSha256,PayloadSha256,PayloadBytes,Currency,Scale,
            EstimateMinorUnits,ReservedMinorUnits,BudgetEvidenceSha256,ApprovedAtUnixMilliseconds,GrantMaximumNewAttempts,GrantMaximumAdditionalMinorUnits)
            VALUES($id,$logical,$document,$revision,$text,$rootKey,$rootState,$admission,$account,$project,$operation,$previous,$previousHash,$child,$review,$envelope,$financial,$payload,$bytes,
            $currency,$scale,$estimate,$reserved,$evidence,$approved,$grantAttempts,$grantMinor);
            """, ("$id", id.ToString("N")), ("$logical", p.LogicalItemId), ("$account", i.Envelope.AccountId), ("$project", i.ProjectId),
            ("$document", i.DocumentId.ToString("D")), ("$revision", i.RevisionId.ToString("D")), ("$text", TextHash(i.CompiledPayload)),
            ("$rootKey", p.AssociationProof.RootHistoricalKey), ("$rootState", p.AssociationProof.RootHistoricalStateSha256), ("$admission", p.AssociationProof.AdmissionSha256),
            ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$previous", i.PreviousIdempotencyKey), ("$previousHash", Digest(p.PreviousState)),
            ("$child", child), ("$review", p.ReviewSha256), ("$envelope", JsonSerializer.Serialize(i.Envelope with { RecoveryAuthorizationId = id })), ("$financial", financial),
            ("$payload", i.Envelope.CompiledPayloadSha256), ("$bytes", i.Envelope.CompiledPayloadBytes), ("$currency", i.Currency), ("$scale", i.Scale),
            ("$estimate", i.EstimateMinorUnits), ("$reserved", p.AdditionalMaximumMinorUnits), ("$evidence", p.Budget.EvidenceSha256),
            ("$approved", timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$grantAttempts", p.AssociationProof.MaximumNewAttempts), ("$grantMinor", p.AssociationProof.MaximumAdditionalReservationMinorUnits));
        await approval.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

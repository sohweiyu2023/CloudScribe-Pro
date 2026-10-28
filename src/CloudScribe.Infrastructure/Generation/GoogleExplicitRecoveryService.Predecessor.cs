using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    private async Task<GoogleRecoveryAssociationProof> ResolveLogicalIdentityAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryIntent intent, GoogleGenerationPersistedQueueState previous, CancellationToken cancellationToken)
    {
        using (var child = Command(connection, transaction, """
            SELECT LogicalItemId,ProjectId,DocumentId,RevisionId,TextSha256,PayloadSha256,RootHistoricalKey,RootHistoricalStateSha256,AdmissionSha256,GrantMaximumNewAttempts,GrantMaximumAdditionalMinorUnits FROM google_recovery_attempts
            WHERE AccountId=$account AND OperationId=$operation AND ChildKey=$previous;
            """, ("$account", previous.AccountId), ("$operation", previous.OperationStableId), ("$previous", previous.IdempotencyKey)))
        using (var row = await child.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(row.GetString(1), intent.ProjectId, StringComparison.Ordinal)
                    || Guid.Parse(row.GetString(2)) != intent.DocumentId || Guid.Parse(row.GetString(3)) != intent.RevisionId
                    || !string.Equals(row.GetString(4), TextHash(intent.CompiledPayload), StringComparison.Ordinal)
                    || !string.Equals(row.GetString(5), intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Recovery cannot change its retained logical narration association.");
                if (row.IsDBNull(9) || row.IsDBNull(10)) throw new InvalidOperationException("Legacy recovery requires independently reviewed bounded admission.");
                var proof = new GoogleRecoveryAssociationProof(row.GetString(0), row.GetString(6), row.GetString(7), row.GetString(8), row.GetInt32(9), row.GetInt64(10));
                var successor = await predecessorEvidence.ResolveAsync(previous.AccountId, previous.OperationStableId,
                    proof.RootHistoricalKey, proof.RootHistoricalStateSha256, cancellationToken).ConfigureAwait(false);
                if (successor?.Successor is { } link && string.Equals(link.PriorChildKey, previous.IdempotencyKey, StringComparison.Ordinal))
                {
                    if (!string.Equals(successor.LogicalNarrationId, proof.LogicalNarrationId, StringComparison.Ordinal) || !string.Equals(successor.HistoricalKey, proof.RootHistoricalKey, StringComparison.Ordinal) || !string.Equals(successor.HistoricalStateSha256, proof.RootHistoricalStateSha256, StringComparison.Ordinal) || !string.Equals(successor.AccountId, previous.AccountId, StringComparison.Ordinal) || !string.Equals(successor.OperationId, previous.OperationStableId, StringComparison.Ordinal) || !string.Equals(successor.ProjectId, intent.ProjectId, StringComparison.Ordinal) || successor.DocumentId != intent.DocumentId || successor.RevisionId != intent.RevisionId
                        || !string.Equals(successor.TextSha256, TextHash(intent.CompiledPayload), StringComparison.Ordinal) || !string.Equals(successor.OriginalPayloadSha256, intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal) || successor.ExpiresAtUtc <= timeProvider.GetUtcNow() || !IsDigest(successor.AdmissionSha256))
                        throw new InvalidOperationException("Successor narration scope changed.");
                    await RequireSuccessorAsync(connection, transaction, successor, creating: true, cancellationToken).ConfigureAwait(false);
                    return new(successor.LogicalNarrationId, successor.HistoricalKey, successor.HistoricalStateSha256,
                        successor.AdmissionSha256, successor.MaximumNewAttempts, successor.MaximumAdditionalReservationMinorUnits, true);
                }
                await RequireAssociationCurrentAsync(connection, transaction, proof, previous.AccountId, previous.OperationStableId, intent.ProjectId,
                    intent.DocumentId, intent.RevisionId, TextHash(intent.CompiledPayload), intent.Envelope.CompiledPayloadSha256, cancellationToken).ConfigureAwait(false);
                return proof;
            }
        }
        if (previous.IdempotencyKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery history is missing; no new association may be invented.");
        var evidence = await predecessorEvidence.ResolveAsync(previous.AccountId, previous.OperationStableId, previous.IdempotencyKey,
            Digest(previous), cancellationToken).ConfigureAwait(false);
        if (evidence is null || evidence.Successor is not null || string.IsNullOrWhiteSpace(evidence.LogicalNarrationId)
            || !string.Equals(evidence.AccountId, previous.AccountId, StringComparison.Ordinal)
            || !string.Equals(evidence.OperationId, previous.OperationStableId, StringComparison.Ordinal)
            || !string.Equals(evidence.HistoricalKey, previous.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(evidence.HistoricalStateSha256, Digest(previous), StringComparison.Ordinal)
            || !string.Equals(evidence.ProjectId, intent.ProjectId, StringComparison.Ordinal)
            || evidence.DocumentId != intent.DocumentId || evidence.RevisionId != intent.RevisionId
            || !string.Equals(evidence.TextSha256, TextHash(intent.CompiledPayload), StringComparison.Ordinal)
            || !string.Equals(evidence.OriginalPayloadSha256, intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal)
            || evidence.AdmissionSha256.Length != 64 || evidence.AdmissionSha256.Any(c => !char.IsAsciiHexDigit(c))
            || evidence.ExpiresAtUtc <= timeProvider.GetUtcNow() || evidence.MaximumNewAttempts is < 1 or > 200
            || evidence.MaximumAdditionalReservationMinorUnits < 0)
            throw new InvalidOperationException("Independent historical narration association is missing or stale.");
        return new(evidence.LogicalNarrationId, evidence.HistoricalKey, evidence.HistoricalStateSha256, evidence.AdmissionSha256,
            evidence.MaximumNewAttempts, evidence.MaximumAdditionalReservationMinorUnits);
    }

    private async Task RequireAssociationCurrentAsync(SqliteConnection connection, SqliteTransaction transaction, GoogleRecoveryAssociationProof proof, string account, string operation,
        string project, Guid document, Guid revision, string textHash, string payloadHash, CancellationToken cancellationToken)
    {
        var e = await predecessorEvidence.ResolveAsync(account, operation, proof.RootHistoricalKey, proof.RootHistoricalStateSha256, cancellationToken).ConfigureAwait(false);
        if (e is null || !string.Equals(e.LogicalNarrationId, proof.LogicalNarrationId, StringComparison.Ordinal)
            || !string.Equals(e.AdmissionSha256, proof.AdmissionSha256, StringComparison.Ordinal)
            || !string.Equals(e.AccountId, account, StringComparison.Ordinal) || !string.Equals(e.OperationId, operation, StringComparison.Ordinal)
            || !string.Equals(e.HistoricalKey, proof.RootHistoricalKey, StringComparison.Ordinal)
            || !string.Equals(e.HistoricalStateSha256, proof.RootHistoricalStateSha256, StringComparison.Ordinal)
            || !string.Equals(e.ProjectId, project, StringComparison.Ordinal) || e.DocumentId != document || e.RevisionId != revision
            || !string.Equals(e.TextSha256, textHash, StringComparison.Ordinal) || !string.Equals(e.OriginalPayloadSha256, payloadHash, StringComparison.Ordinal)
            || e.ExpiresAtUtc <= timeProvider.GetUtcNow() || e.MaximumNewAttempts != proof.MaximumNewAttempts
            || e.MaximumAdditionalReservationMinorUnits != proof.MaximumAdditionalReservationMinorUnits)
            throw new InvalidOperationException("Independent predecessor admission was revoked or changed.");
        if (e.Successor is not null)
            await RequireSuccessorAsync(connection, transaction, e, creating: false, cancellationToken).ConfigureAwait(false);
    }
}

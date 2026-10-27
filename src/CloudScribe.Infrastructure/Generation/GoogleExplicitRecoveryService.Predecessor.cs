using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    private async Task<GoogleRecoveryAssociationProof> ResolveLogicalIdentityAsync(SqliteConnection connection, SqliteTransaction transaction,
        GoogleRecoveryIntent intent, GoogleGenerationPersistedQueueState previous, CancellationToken cancellationToken)
    {
        using (var child = Command(connection, transaction, """
            SELECT LogicalItemId,ProjectId,DocumentId,RevisionId,TextSha256,PayloadSha256,RootHistoricalKey,RootHistoricalStateSha256,AdmissionSha256 FROM google_recovery_attempts
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
                var proof = new GoogleRecoveryAssociationProof(row.GetString(0), row.GetString(6), row.GetString(7), row.GetString(8));
                await RequireAssociationCurrentAsync(proof, previous.AccountId, previous.OperationStableId, intent.ProjectId,
                    intent.DocumentId, intent.RevisionId, TextHash(intent.CompiledPayload), intent.Envelope.CompiledPayloadSha256, cancellationToken).ConfigureAwait(false);
                return proof;
            }
        }
        if (previous.IdempotencyKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery history is missing; no new association may be invented.");
        var evidence = await predecessorEvidence.ResolveAsync(previous.AccountId, previous.OperationStableId, previous.IdempotencyKey,
            Digest(previous), cancellationToken).ConfigureAwait(false);
        if (evidence is null || string.IsNullOrWhiteSpace(evidence.LogicalNarrationId)
            || !string.Equals(evidence.AccountId, previous.AccountId, StringComparison.Ordinal)
            || !string.Equals(evidence.OperationId, previous.OperationStableId, StringComparison.Ordinal)
            || !string.Equals(evidence.HistoricalKey, previous.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(evidence.HistoricalStateSha256, Digest(previous), StringComparison.Ordinal)
            || !string.Equals(evidence.ProjectId, intent.ProjectId, StringComparison.Ordinal)
            || evidence.DocumentId != intent.DocumentId || evidence.RevisionId != intent.RevisionId
            || !string.Equals(evidence.TextSha256, TextHash(intent.CompiledPayload), StringComparison.Ordinal)
            || !string.Equals(evidence.OriginalPayloadSha256, intent.Envelope.CompiledPayloadSha256, StringComparison.Ordinal)
            || evidence.AdmissionSha256.Length != 64 || evidence.AdmissionSha256.Any(c => !char.IsAsciiHexDigit(c))
            || evidence.ExpiresAtUtc <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Independent historical narration association is missing or stale.");
        return new(evidence.LogicalNarrationId, evidence.HistoricalKey, evidence.HistoricalStateSha256, evidence.AdmissionSha256);
    }

    private async Task RequireAssociationCurrentAsync(GoogleRecoveryAssociationProof proof, string account, string operation,
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
            || e.ExpiresAtUtc <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Independent predecessor admission was revoked or changed.");
    }
}

using System.Text.Json;
using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    public async Task<string> FindLatestKeyAsync(GoogleRecoveryPredecessorEvidence admittedRoot, CancellationToken cancellationToken = default)
    {
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ChildKey FROM google_recovery_attempts WHERE LogicalItemId=$logical AND AccountId=$account AND ProjectId=$project ORDER BY rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$logical", admittedRoot.LogicalNarrationId);
        command.Parameters.AddWithValue("$account", admittedRoot.AccountId);
        command.Parameters.AddWithValue("$project", admittedRoot.ProjectId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? admittedRoot.HistoricalKey;
    }

    /// <summary>Fresh durable lookup. A selected ID/envelope cannot create authority, nor fall back to ordinary approval.</summary>
    public async Task<GoogleRecoveryExecutionAuthorization?> LoadExecutionAuthorizationAsync(
        GoogleGenerationSubmissionEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.RecoveryAuthorizationId is not { } id) return null;
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = Command(connection, transaction, """
            SELECT ChildKey,ReviewSha256,ProjectId,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,Consumed,EnvelopeJson,
            LogicalItemId,RootHistoricalKey,RootHistoricalStateSha256,AdmissionSha256,DocumentId,RevisionId,TextSha256,ApprovedFinancialSha256,GrantMaximumNewAttempts,GrantMaximumAdditionalMinorUnits
            FROM google_recovery_attempts WHERE AuthorizationId=$id AND AccountId=$account AND OperationId=$operation;
            """, ("$id", id.ToString("N")), ("$account", envelope.AccountId), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId));
        GoogleRecoveryExecutionAuthorization result; GoogleRecoveryAssociationProof association;
        Guid documentId, revisionId; string textHash, financial;
        using (var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || row.GetInt64(7) != 0
                || !string.Equals(row.GetString(8), JsonSerializer.Serialize(envelope), StringComparison.Ordinal)
                || !string.Equals(row.GetString(0), KeyPrefix + id.ToString("N"), StringComparison.Ordinal)) return null;
            var approval = new GoogleRecoveryApprovedAttempt(id, row.GetString(0), row.GetString(1), row.GetInt64(6));
            var spend = GoogleGenerationSpendAuthorization.Create(envelope, row.GetString(3), row.GetInt32(4), row.GetInt64(5), row.GetInt64(6));
            result = new(approval, row.GetString(2), spend, row.GetString(16));
            if (row.IsDBNull(17) || row.IsDBNull(18)) return null;
            association = new(row.GetString(9), row.GetString(10), row.GetString(11), row.GetString(12), row.GetInt32(17), row.GetInt64(18));
            documentId = Guid.Parse(row.GetString(13)); revisionId = Guid.Parse(row.GetString(14)); textHash = row.GetString(15); financial = row.GetString(16);
        }
        if (!string.Equals(await FinancialDigestAsync(connection, transaction, cancellationToken).ConfigureAwait(false), financial, StringComparison.Ordinal))
            throw new InvalidOperationException("Financial evidence changed after recovery approval.");
        await RequireAssociationCurrentAsync(connection, transaction, association, envelope.AccountId, GoogleGenerationProvider.SynthesizeOperationStableId,
            result.ProjectId, documentId, revisionId, textHash, envelope.CompiledPayloadSha256, cancellationToken).ConfigureAwait(false);
        await RequireGrantCapacityAsync(connection, transaction, association, envelope.AccountId, result.ProjectId, 0, creating: false, cancellationToken).ConfigureAwait(false);
        var budget = await RequireCurrentBudgetAsync(connection, transaction, envelope.AccountId, result.ProjectId, result.SpendAuthorization.Currency,
            result.SpendAuthorization.Scale, financial, cancellationToken).ConfigureAwait(false);
        return result with { Budget = budget };
    }

    public async Task<bool> TryClaimAuthorizedAsync(GoogleGenerationSubmissionEnvelope envelope, string projectId,
        string currency, int scale, long estimate, GoogleGenerationPersistedQueueState claim,
        CancellationToken cancellationToken = default)
    {
        var authorization = await LoadExecutionAuthorizationAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (authorization is null || !string.Equals(authorization.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(authorization.Approval.IdempotencyKey, claim.IdempotencyKey, StringComparison.Ordinal)) return false;
        authorization.SpendAuthorization.EnsureStillAuthorized(envelope, currency, scale, estimate);
        return await TryClaimAsync(authorization.Approval, claim, envelope, projectId, currency, scale, estimate, cancellationToken).ConfigureAwait(false);
    }
}

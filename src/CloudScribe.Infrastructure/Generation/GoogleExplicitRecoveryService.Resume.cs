using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleExplicitRecoveryService
{
    /// <summary>Read-only discovery of the SAME pending reservation; current authority is revalidated by exact-ID lookup.</summary>
    public async Task<GoogleRecoveryExecutionAuthorization?> LoadPendingForCurrentEnvelopeAsync(
        GoogleGenerationSubmissionEnvelope currentEnvelope, string projectId, Guid documentId, Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        if (currentEnvelope.RecoveryAuthorizationId is not null) throw new InvalidOperationException("Current compilation must not self-assign recovery authority.");
        Guid? id = null;
        using (var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT AuthorizationId FROM google_recovery_attempts WHERE AccountId=$account AND ProjectId=$project AND DocumentId=$document AND RevisionId=$revision AND Consumed=0 LIMIT 2;";
            command.Parameters.AddWithValue("$account", currentEnvelope.AccountId); command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$document", documentId.ToString("D")); command.Parameters.AddWithValue("$revision", revisionId.ToString("D"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) id = Guid.ParseExact(reader.GetString(0), "N");
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Ambiguous pending recovery requires reconciliation.");
        }
        return id is null ? null : await LoadExecutionAuthorizationAsync(currentEnvelope with { RecoveryAuthorizationId = id }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A pending recovery exists but its exact current envelope or authority changed. Preserve its reservation and reconcile it.");
    }
}

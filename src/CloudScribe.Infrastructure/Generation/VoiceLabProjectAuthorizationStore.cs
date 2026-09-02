using System.Data.Common;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabProjectAuthorizationStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory) : IVoiceLabProjectAuthorizationStore
{
    public async Task<VoiceLabProjectAuthorizationEvidence?> LoadCurrentAsync(
        string providerId,
        string accountId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    SELECT AccountRevision, CredentialReferenceId, CapabilityEvidenceId,
                           ProjectAuthorized, PrivateVoiceAccessAuthorized,
                           CapturedAtUnixMilliseconds, ExpiresAtUnixMilliseconds
                    FROM voice_lab_project_authorizations
                    WHERE ProviderId = @providerId AND AccountId = @accountId AND ProjectId = @projectId
                    LIMIT 1;
                    """;
                AddParameter(command, "@providerId", providerId);
                AddParameter(command, "@accountId", accountId);
                AddParameter(command, "@projectId", projectId);

                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        return null;

                    return new VoiceLabProjectAuthorizationEvidence(
                        providerId,
                        accountId,
                        projectId,
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
                }
            }
        }
    }

    public async Task SaveVerifiedAsync(
        VoiceLabProjectAuthorizationEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.AccountRevision < 1)
            throw new InvalidOperationException("Voice Lab project authorization requires a positive account revision.");
        if (!evidence.ProjectAuthorized)
            throw new InvalidOperationException("Only positively verified Voice Lab project authorization may be persisted.");
        if (evidence.CapturedAtUtc.Offset != TimeSpan.Zero || evidence.ExpiresAtUtc.Offset != TimeSpan.Zero || evidence.ExpiresAtUtc <= evidence.CapturedAtUtc)
            throw new InvalidOperationException("Voice Lab project authorization requires a bounded UTC validity window.");
        ArgumentException.ThrowIfNullOrWhiteSpace(
            evidence.CredentialReferenceId,
            nameof(evidence.CredentialReferenceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(
            evidence.CapabilityEvidenceId,
            nameof(evidence.CapabilityEvidenceId));

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    INSERT INTO voice_lab_project_authorizations (
                        ProviderId, AccountId, ProjectId, AccountRevision,
                        CredentialReferenceId, CapabilityEvidenceId,
                        ProjectAuthorized, PrivateVoiceAccessAuthorized,
                        CapturedAtUnixMilliseconds, ExpiresAtUnixMilliseconds)
                    VALUES (
                        @providerId, @accountId, @projectId, @accountRevision,
                        @credentialReferenceId, @capabilityEvidenceId,
                        @projectAuthorized, @privateVoiceAccessAuthorized,
                        @capturedAt, @expiresAt)
                    ON CONFLICT(ProviderId, AccountId, ProjectId) DO UPDATE SET
                        AccountRevision = excluded.AccountRevision,
                        CredentialReferenceId = excluded.CredentialReferenceId,
                        CapabilityEvidenceId = excluded.CapabilityEvidenceId,
                        ProjectAuthorized = excluded.ProjectAuthorized,
                        PrivateVoiceAccessAuthorized = excluded.PrivateVoiceAccessAuthorized,
                        CapturedAtUnixMilliseconds = excluded.CapturedAtUnixMilliseconds,
                        ExpiresAtUnixMilliseconds = excluded.ExpiresAtUnixMilliseconds;
                    """;
                AddParameter(command, "@providerId", evidence.ProviderId);
                AddParameter(command, "@accountId", evidence.AccountId);
                AddParameter(command, "@projectId", evidence.ProjectId);
                AddParameter(command, "@accountRevision", evidence.AccountRevision);
                AddParameter(command, "@credentialReferenceId", evidence.CredentialReferenceId);
                AddParameter(command, "@capabilityEvidenceId", evidence.CapabilityEvidenceId);
                AddParameter(command, "@projectAuthorized", evidence.ProjectAuthorized);
                AddParameter(command, "@privateVoiceAccessAuthorized", evidence.PrivateVoiceAccessAuthorized);
                AddParameter(command, "@capturedAt", evidence.CapturedAtUtc.ToUnixTimeMilliseconds());
                AddParameter(command, "@expiresAt", evidence.ExpiresAtUtc.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

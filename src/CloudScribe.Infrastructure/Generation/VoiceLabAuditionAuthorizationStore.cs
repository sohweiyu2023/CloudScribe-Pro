using System.Data.Common;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabAuditionAuthorizationStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory) : IVoiceLabAuditionAuthorizationStore
{
    public async Task<VoiceLabAuditionPersistedAuthorization?> LoadCurrentAsync(
        VoiceLabCatalogSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection.Validate();

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    SELECT VoiceFingerprint, CapabilityEvidenceId, CredentialReferenceId,
                           PricingEvidenceId, SpendAuthorizationId, AccountRevision,
                           CapturedAtUnixMilliseconds, ExpiresAtUnixMilliseconds
                    FROM voice_lab_audition_authorizations
                    WHERE ProviderId = @providerId
                      AND AccountId = @accountId
                      AND ProjectId = @projectId
                      AND VoiceId = @voiceId
                    LIMIT 1;
                    """;
                AddParameter(command, "@providerId", selection.ProviderStableId);
                AddParameter(command, "@accountId", selection.AccountStableId);
                AddParameter(command, "@projectId", selection.ProjectStableId);
                AddParameter(command, "@voiceId", selection.VoiceStableId);

                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        return null;

                    string voiceFingerprint = reader.GetString(0);
                    string capabilityEvidenceId = reader.GetString(1);
                    if (!string.Equals(voiceFingerprint, selection.VoiceFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(capabilityEvidenceId, selection.CapabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Persisted Voice Lab audition authorization is bound to different current voice/capability evidence.");
                    }

                    return new VoiceLabAuditionPersistedAuthorization(
                        selection,
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt64(5),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
                }
            }
        }
    }

    public async Task SaveVerifiedAsync(
        VoiceLabAuditionPersistedAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Selection.Validate();
        _ = authorization.ToCurrentEvidence(authorization.CapturedAtUtc);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    INSERT INTO voice_lab_audition_authorizations (
                        ProviderId, AccountId, ProjectId, VoiceId, VoiceFingerprint,
                        CapabilityEvidenceId, CredentialReferenceId, PricingEvidenceId,
                        SpendAuthorizationId, AccountRevision,
                        CapturedAtUnixMilliseconds, ExpiresAtUnixMilliseconds)
                    VALUES (
                        @providerId, @accountId, @projectId, @voiceId, @voiceFingerprint,
                        @capabilityEvidenceId, @credentialReferenceId, @pricingEvidenceId,
                        @spendAuthorizationId, @accountRevision, @capturedAt, @expiresAt)
                    ON CONFLICT(ProviderId, AccountId, ProjectId, VoiceId) DO UPDATE SET
                        VoiceFingerprint = excluded.VoiceFingerprint,
                        CapabilityEvidenceId = excluded.CapabilityEvidenceId,
                        CredentialReferenceId = excluded.CredentialReferenceId,
                        PricingEvidenceId = excluded.PricingEvidenceId,
                        SpendAuthorizationId = excluded.SpendAuthorizationId,
                        AccountRevision = excluded.AccountRevision,
                        CapturedAtUnixMilliseconds = excluded.CapturedAtUnixMilliseconds,
                        ExpiresAtUnixMilliseconds = excluded.ExpiresAtUnixMilliseconds;
                    """;
                AddParameter(command, "@providerId", authorization.Selection.ProviderStableId);
                AddParameter(command, "@accountId", authorization.Selection.AccountStableId);
                AddParameter(command, "@projectId", authorization.Selection.ProjectStableId);
                AddParameter(command, "@voiceId", authorization.Selection.VoiceStableId);
                AddParameter(command, "@voiceFingerprint", authorization.Selection.VoiceFingerprint);
                AddParameter(command, "@capabilityEvidenceId", authorization.Selection.CapabilityEvidenceId);
                AddParameter(command, "@credentialReferenceId", authorization.CredentialReferenceId);
                AddParameter(command, "@pricingEvidenceId", authorization.PricingEvidenceId);
                AddParameter(command, "@spendAuthorizationId", authorization.SpendAuthorizationId);
                AddParameter(command, "@accountRevision", authorization.AccountRevision);
                AddParameter(command, "@capturedAt", authorization.CapturedAtUtc.ToUnixTimeMilliseconds());
                AddParameter(command, "@expiresAt", authorization.ExpiresAtUtc.ToUnixTimeMilliseconds());
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

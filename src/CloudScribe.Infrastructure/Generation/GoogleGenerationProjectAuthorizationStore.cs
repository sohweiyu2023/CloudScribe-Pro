using System.Data.Common;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationProjectAuthorizationStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory) : IGoogleGenerationProjectAuthorizationStore
{
    public async Task<GoogleGenerationProjectAuthorizationEvidence?> LoadCurrentAsync(
        string accountId,
        string projectId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    SELECT CredentialReferenceId, CapabilityProvenanceId, EndpointId, RegionId,
                           EndpointOrigin, Authorized, CapturedAtUnixMilliseconds, ExpiresAtUnixMilliseconds
                    FROM google_generation_project_authorizations
                    WHERE AccountId = @accountId AND ProjectId = @projectId AND ModelId = @modelId
                    LIMIT 1;
                    """;
                AddParameter(command, "@accountId", accountId);
                AddParameter(command, "@projectId", projectId);
                AddParameter(command, "@modelId", modelId);

                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        return null;

                    return new GoogleGenerationProjectAuthorizationEvidence(
                        accountId,
                        projectId,
                        modelId,
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
                }
            }
        }
    }

    public async Task SaveVerifiedAsync(
        GoogleGenerationProjectAuthorizationEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate(DateTimeOffset.UtcNow);
        if (!evidence.Authorized)
            throw new InvalidOperationException("Only positively verified Google project/model authorization may be persisted.");

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    INSERT INTO google_generation_project_authorizations (
                        AccountId, ProjectId, ModelId, CredentialReferenceId, CapabilityProvenanceId,
                        EndpointId, RegionId, EndpointOrigin, Authorized,
                        CapturedAtUnixMilliseconds, ExpiresAtUnixMilliseconds)
                    VALUES (
                        @accountId, @projectId, @modelId, @credentialReferenceId, @capabilityProvenanceId,
                        @endpointId, @regionId, @endpointOrigin, @authorized, @capturedAt, @expiresAt)
                    ON CONFLICT(AccountId, ProjectId, ModelId) DO UPDATE SET
                        CredentialReferenceId = excluded.CredentialReferenceId,
                        CapabilityProvenanceId = excluded.CapabilityProvenanceId,
                        EndpointId = excluded.EndpointId,
                        RegionId = excluded.RegionId,
                        EndpointOrigin = excluded.EndpointOrigin,
                        Authorized = excluded.Authorized,
                        CapturedAtUnixMilliseconds = excluded.CapturedAtUnixMilliseconds,
                        ExpiresAtUnixMilliseconds = excluded.ExpiresAtUnixMilliseconds;
                    """;
                AddParameter(command, "@accountId", evidence.AccountId);
                AddParameter(command, "@projectId", evidence.ProjectId);
                AddParameter(command, "@modelId", evidence.ModelId);
                AddParameter(command, "@credentialReferenceId", evidence.CredentialReferenceId);
                AddParameter(command, "@capabilityProvenanceId", evidence.CapabilityProvenanceId);
                AddParameter(command, "@endpointId", evidence.EndpointId);
                AddParameter(command, "@regionId", evidence.RegionId);
                AddParameter(command, "@endpointOrigin", evidence.EndpointOrigin);
                AddParameter(command, "@authorized", evidence.Authorized);
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

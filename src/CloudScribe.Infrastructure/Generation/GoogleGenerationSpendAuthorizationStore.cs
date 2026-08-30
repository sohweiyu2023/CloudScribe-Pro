using System.Data.Common;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationSpendAuthorizationStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory,
    TimeProvider timeProvider) : IGoogleGenerationSpendAuthorizationStore
{
    public async Task SaveApprovedAsync(
        GoogleGenerationSpendAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                DbCommand delete = CreateEnvelopeCommand(connection, authorization.Envelope);
                await using (delete.ConfigureAwait(false))
                {
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM google_generation_spend_authorizations WHERE {EnvelopePredicate};";
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                DbCommand insert = connection.CreateCommand();
                await using (insert.ConfigureAwait(false))
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO google_generation_spend_authorizations (
                            Id, AccountId, CredentialReferenceId, CapabilityProvenanceId, PricingProvenanceId,
                            RequestRevision, VoiceName, AudioEncoding, CompiledPayloadSha256, CompiledPayloadBytes,
                            Currency, Scale, AuthorizedMaximumMinorUnits, ApprovedEstimateMinorUnits, ApprovedAtUnixMilliseconds)
                        VALUES (
                            @id, @accountId, @credentialReferenceId, @capabilityProvenanceId, @pricingProvenanceId,
                            @requestRevision, @voiceName, @audioEncoding, @compiledPayloadSha256, @compiledPayloadBytes,
                            @currency, @scale, @authorizedMaximumMinorUnits, @approvedEstimateMinorUnits, @approvedAt);
                        """;
                    AddEnvelopeParameters(insert, authorization.Envelope);
                    AddParameter(insert, "@id", Guid.NewGuid().ToString("D"));
                    AddParameter(insert, "@currency", authorization.Currency);
                    AddParameter(insert, "@scale", authorization.Scale);
                    AddParameter(insert, "@authorizedMaximumMinorUnits", authorization.AuthorizedMaximumMinorUnits);
                    AddParameter(insert, "@approvedEstimateMinorUnits", authorization.ApprovedEstimateMinorUnits);
                    AddParameter(insert, "@approvedAt", timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(
        GoogleGenerationSubmissionEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            DbConnection connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            DbCommand command = CreateEnvelopeCommand(connection, envelope);
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"SELECT Currency, Scale, AuthorizedMaximumMinorUnits, ApprovedEstimateMinorUnits FROM google_generation_spend_authorizations WHERE {EnvelopePredicate} LIMIT 1;";

                DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return GoogleGenerationSpendAuthorization.Create(
                        envelope,
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetInt64(3),
                        reader.GetInt64(2));
                }
            }
        }
    }

    private const string EnvelopePredicate = """
        AccountId = @accountId
        AND CredentialReferenceId = @credentialReferenceId
        AND CapabilityProvenanceId = @capabilityProvenanceId
        AND PricingProvenanceId = @pricingProvenanceId
        AND RequestRevision = @requestRevision
        AND VoiceName = @voiceName
        AND AudioEncoding = @audioEncoding
        AND CompiledPayloadSha256 = @compiledPayloadSha256
        AND CompiledPayloadBytes = @compiledPayloadBytes
        """;

    private static DbCommand CreateEnvelopeCommand(DbConnection connection, GoogleGenerationSubmissionEnvelope envelope)
    {
        DbCommand command = connection.CreateCommand();
        AddEnvelopeParameters(command, envelope);
        return command;
    }

    private static void AddEnvelopeParameters(DbCommand command, GoogleGenerationSubmissionEnvelope envelope)
    {
        AddParameter(command, "@accountId", envelope.AccountId);
        AddParameter(command, "@credentialReferenceId", envelope.CredentialReferenceId);
        AddParameter(command, "@capabilityProvenanceId", envelope.CapabilityProvenanceId);
        AddParameter(command, "@pricingProvenanceId", envelope.PricingProvenanceId);
        AddParameter(command, "@requestRevision", envelope.RequestRevision);
        AddParameter(command, "@voiceName", envelope.VoiceName);
        AddParameter(command, "@audioEncoding", envelope.AudioEncoding);
        AddParameter(command, "@compiledPayloadSha256", envelope.CompiledPayloadSha256);
        AddParameter(command, "@compiledPayloadBytes", envelope.CompiledPayloadBytes);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

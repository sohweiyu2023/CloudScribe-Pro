using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public interface IGoogleGenerationSpendAuthorizationStore
{
    Task SaveApprovedAsync(
        GoogleGenerationSpendAuthorization authorization,
        CancellationToken cancellationToken = default);

    Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(
        GoogleGenerationSubmissionEnvelope envelope,
        CancellationToken cancellationToken = default);
}

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
            GoogleGenerationSubmissionEnvelope envelope = authorization.Envelope;
            GoogleGenerationSpendAuthorizationEntity? existing = await FindAsync(context, envelope, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                existing.Currency = authorization.Currency;
                existing.Scale = authorization.Scale;
                existing.AuthorizedMaximumMinorUnits = authorization.AuthorizedMaximumMinorUnits;
                existing.ApprovedEstimateMinorUnits = authorization.ApprovedEstimateMinorUnits;
                existing.ApprovedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            }
            else
            {
                context.GoogleGenerationSpendAuthorizations.Add(new GoogleGenerationSpendAuthorizationEntity
                {
                    Id = Guid.NewGuid(),
                    AccountId = envelope.AccountId,
                    CredentialReferenceId = envelope.CredentialReferenceId,
                    CapabilityProvenanceId = envelope.CapabilityProvenanceId,
                    PricingProvenanceId = envelope.PricingProvenanceId,
                    RequestRevision = envelope.RequestRevision,
                    VoiceName = envelope.VoiceName,
                    AudioEncoding = envelope.AudioEncoding,
                    CompiledPayloadSha256 = envelope.CompiledPayloadSha256,
                    CompiledPayloadBytes = envelope.CompiledPayloadBytes,
                    Currency = authorization.Currency,
                    Scale = authorization.Scale,
                    AuthorizedMaximumMinorUnits = authorization.AuthorizedMaximumMinorUnits,
                    ApprovedEstimateMinorUnits = authorization.ApprovedEstimateMinorUnits,
                    ApprovedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                });
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            GoogleGenerationSpendAuthorizationEntity? entity = await FindAsync(context, envelope, cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                return null;
            }

            return GoogleGenerationSpendAuthorization.Create(
                envelope,
                entity.Currency,
                entity.Scale,
                entity.ApprovedEstimateMinorUnits,
                entity.AuthorizedMaximumMinorUnits);
        }
    }

    private static Task<GoogleGenerationSpendAuthorizationEntity?> FindAsync(
        CloudScribeDbContext context,
        GoogleGenerationSubmissionEnvelope envelope,
        CancellationToken cancellationToken) => context.GoogleGenerationSpendAuthorizations.SingleOrDefaultAsync(
            item => item.AccountId == envelope.AccountId
                && item.CredentialReferenceId == envelope.CredentialReferenceId
                && item.CapabilityProvenanceId == envelope.CapabilityProvenanceId
                && item.PricingProvenanceId == envelope.PricingProvenanceId
                && item.RequestRevision == envelope.RequestRevision
                && item.VoiceName == envelope.VoiceName
                && item.AudioEncoding == envelope.AudioEncoding
                && item.CompiledPayloadSha256 == envelope.CompiledPayloadSha256
                && item.CompiledPayloadBytes == envelope.CompiledPayloadBytes,
            cancellationToken);
}

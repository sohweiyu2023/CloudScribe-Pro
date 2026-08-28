using System.Data;
using System.Security.Cryptography;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class EfPricingCatalogHistoryStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory,
    TimeProvider timeProvider) : IPricingCatalogHistoryStore
{
    public async Task<PricingCatalogSnapshot> SaveSnapshotAsync(
        ReadOnlyMemory<byte> utf8Catalog,
        PricingCatalogTrustState trustState,
        PricingCatalogSource source,
        string? signatureKeyId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (utf8Catalog.IsEmpty)
        {
            throw new ArgumentException("Admitted pricing catalog bytes cannot be empty.", nameof(utf8Catalog));
        }
        if (trustState is not (PricingCatalogTrustState.ValidUnsigned or PricingCatalogTrustState.SignatureVerified))
        {
            throw new InvalidOperationException("Only contract-valid pricing catalogs can enter admitted history.");
        }

        string? normalizedKeyId = NormalizeSignatureKeyId(trustState, signatureKeyId);
        byte[] catalogBytes = utf8Catalog.ToArray();
        string sha256 = Convert.ToHexString(SHA256.HashData(catalogBytes)).ToLowerInvariant();

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            PricingCatalogSnapshotEntity? existing = await context.PricingCatalogSnapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Sha256 == sha256, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.CatalogBytes.AsSpan().SequenceEqual(catalogBytes))
                {
                    throw new InvalidDataException("Stored catalog bytes do not match their SHA-256 identity.");
                }
                return MapSnapshot(existing);
            }

            PricingCatalogSnapshotEntity entity = new()
            {
                Id = Guid.NewGuid(),
                Sha256 = sha256,
                CatalogBytes = catalogBytes,
                TrustState = (int)trustState,
                SourceKind = (int)source.Kind,
                SourceLabel = source.Label,
                CapturedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                SignatureKeyId = normalizedKeyId,
            };
            context.PricingCatalogSnapshots.Add(entity);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return MapSnapshot(entity);
        }
    }

    public async Task<IReadOnlyList<PricingCatalogSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            List<PricingCatalogSnapshotEntity> entities = await context.PricingCatalogSnapshots
                .AsNoTracking()
                .OrderByDescending(item => item.CapturedAtUnixMilliseconds)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(MapSnapshot).ToArray();
        }
    }

    public async Task<PricingCatalogSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default)
    {
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            PricingCatalogActivationEntity? activation = await context.PricingCatalogActivations
                .AsNoTracking()
                .OrderByDescending(item => item.Sequence)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (activation is null)
            {
                return null;
            }

            PricingCatalogSnapshotEntity snapshot = await context.PricingCatalogSnapshots
                .AsNoTracking()
                .SingleAsync(item => item.Id == activation.SnapshotId, cancellationToken)
                .ConfigureAwait(false);
            return MapSnapshot(snapshot);
        }
    }

    public async Task<IReadOnlyList<PricingCatalogActivation>> ListActivationsAsync(CancellationToken cancellationToken = default)
    {
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            List<PricingCatalogActivationEntity> entities = await context.PricingCatalogActivations
                .AsNoTracking()
                .OrderByDescending(item => item.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(MapActivation).ToArray();
        }
    }

    public async Task<PricingCatalogActivation> ActivateAsync(
        PricingCatalogActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserConfirmed)
        {
            throw new InvalidOperationException("Catalog activation requires an explicit user confirmation and can never occur silently.");
        }

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            IDbContextTransaction transaction = await context.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                PricingCatalogSnapshotEntity snapshot = await LoadSnapshotForActivationAsync(
                    context,
                    request,
                    cancellationToken).ConfigureAwait(false);
                PricingCatalogActivationEntity? current = await LoadCurrentActivationAsync(
                    context,
                    cancellationToken).ConfigureAwait(false);

                ValidateActivationState(snapshot, current, request);
                ValidateApproval(snapshot, request.ApprovalKind);
                await ValidateRollbackTargetAsync(context, snapshot, request.Kind, cancellationToken).ConfigureAwait(false);

                PricingCatalogActivationEntity activation = CreateActivation(snapshot, current, request);
                context.PricingCatalogActivations.Add(activation);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return MapActivation(activation);
            }
        }
    }

    private static async Task<PricingCatalogSnapshotEntity> LoadSnapshotForActivationAsync(
        CloudScribeDbContext context,
        PricingCatalogActivationRequest request,
        CancellationToken cancellationToken)
    {
        PricingCatalogSnapshotEntity snapshot = await context.PricingCatalogSnapshots
            .SingleOrDefaultAsync(item => item.Id == request.SnapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Pricing catalog snapshot was not found.");
        if (!string.Equals(snapshot.Sha256, request.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog snapshot changed from the SHA-256 identity approved by the caller.");
        }

        return snapshot;
    }

    private static async Task<PricingCatalogActivationEntity?> LoadCurrentActivationAsync(
        CloudScribeDbContext context,
        CancellationToken cancellationToken) =>
        await context.PricingCatalogActivations
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static void ValidateActivationState(
        PricingCatalogSnapshotEntity snapshot,
        PricingCatalogActivationEntity? current,
        PricingCatalogActivationRequest request)
    {
        long currentSequence = current?.Sequence ?? 0;
        if (currentSequence != request.ExpectedCurrentActivationSequence)
        {
            throw new DBConcurrencyException("Active pricing catalog changed before this activation request could commit.");
        }

        if (current?.SnapshotId == snapshot.Id)
        {
            throw new InvalidOperationException("Requested pricing catalog is already active.");
        }
    }

    private static async Task ValidateRollbackTargetAsync(
        CloudScribeDbContext context,
        PricingCatalogSnapshotEntity snapshot,
        PricingCatalogActivationKind kind,
        CancellationToken cancellationToken)
    {
        if (kind != PricingCatalogActivationKind.Rollback)
        {
            return;
        }

        bool wasPreviouslyActive = await context.PricingCatalogActivations
            .AsNoTracking()
            .AnyAsync(item => item.SnapshotId == snapshot.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!wasPreviouslyActive)
        {
            throw new InvalidOperationException("Rollback can target only a pricing catalog snapshot that was active previously.");
        }
    }

    private PricingCatalogActivationEntity CreateActivation(
        PricingCatalogSnapshotEntity snapshot,
        PricingCatalogActivationEntity? current,
        PricingCatalogActivationRequest request) => new()
        {
            SnapshotId = snapshot.Id,
            PreviousSnapshotId = current?.SnapshotId,
            Kind = (int)request.Kind,
            ApprovalKind = (int)request.ApprovalKind,
            Reason = request.Reason,
            OccurredAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

    private static void ValidateApproval(PricingCatalogSnapshotEntity snapshot, PricingCatalogApprovalKind approvalKind)
    {
        PricingCatalogTrustState trustState = (PricingCatalogTrustState)snapshot.TrustState;
        if (trustState == PricingCatalogTrustState.ValidUnsigned && approvalKind != PricingCatalogApprovalKind.ManualUnsigned)
        {
            throw new InvalidOperationException("Unsigned catalogs require explicit manual approval.");
        }
        if (trustState == PricingCatalogTrustState.SignatureVerified && approvalKind != PricingCatalogApprovalKind.VerifiedSignature)
        {
            throw new InvalidOperationException("Signature-verified catalogs must retain verified-signature provenance when activated.");
        }
    }

    private static string? NormalizeSignatureKeyId(PricingCatalogTrustState trustState, string? signatureKeyId)
    {
        if (trustState == PricingCatalogTrustState.ValidUnsigned)
        {
            if (signatureKeyId is not null)
            {
                throw new ArgumentException("Unsigned catalog history cannot carry a signature key id.", nameof(signatureKeyId));
            }
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(signatureKeyId);
        string normalized = signatureKeyId.Trim();
        if (normalized.Length > 160 || normalized.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("Signature key id must be 1-160 visible characters.", nameof(signatureKeyId));
        }
        return normalized;
    }

    private static PricingCatalogSnapshot MapSnapshot(PricingCatalogSnapshotEntity entity) => new(
        entity.Id,
        entity.Sha256,
        entity.CatalogBytes.LongLength,
        (PricingCatalogTrustState)entity.TrustState,
        new PricingCatalogSource((PricingCatalogSourceKind)entity.SourceKind, entity.SourceLabel),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CapturedAtUnixMilliseconds),
        entity.SignatureKeyId);

    private static PricingCatalogActivation MapActivation(PricingCatalogActivationEntity entity) => new(
        entity.Sequence,
        entity.SnapshotId,
        entity.PreviousSnapshotId,
        (PricingCatalogActivationKind)entity.Kind,
        (PricingCatalogApprovalKind)entity.ApprovalKind,
        entity.Reason,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.OccurredAtUnixMilliseconds));
}

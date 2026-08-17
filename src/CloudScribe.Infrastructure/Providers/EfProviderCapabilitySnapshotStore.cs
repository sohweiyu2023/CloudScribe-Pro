using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Providers;

public sealed class EfProviderCapabilitySnapshotStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory) : IProviderCapabilitySnapshotStore
{
    public async Task<StoredProviderCapabilitySnapshot> SaveAsync(
        ProviderCapabilitySnapshot snapshot,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StoredProviderCapabilitySnapshot candidate = new(Guid.NewGuid(), snapshot, expiresAtUtc);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            bool accountExists = await context.ProviderAccounts
                .AsNoTracking()
                .AnyAsync(item => item.ProviderStableId == snapshot.Account.ProviderStableId && item.AccountId == snapshot.Account.AccountId, cancellationToken)
                .ConfigureAwait(false);
            if (!accountExists)
            {
                throw new InvalidOperationException("Capability evidence cannot be persisted for an unregistered provider account.");
            }

            ProviderCapabilitySnapshotEntity entity = new()
            {
                Id = candidate.Id,
                ProviderStableId = snapshot.Account.ProviderStableId,
                AccountId = snapshot.Account.AccountId,
                AccountDisplayName = snapshot.Account.DisplayName,
                CredentialTargetName = snapshot.Account.CredentialReference?.TargetName,
                EndpointId = snapshot.Account.EndpointId,
                RegionId = snapshot.Account.RegionId,
                CapturedAtUnixMilliseconds = snapshot.CapturedAtUtc.ToUnixTimeMilliseconds(),
                ExpiresAtUnixMilliseconds = expiresAtUtc.ToUnixTimeMilliseconds(),
                ProvenanceId = snapshot.ProvenanceId,
            };
            context.ProviderCapabilitySnapshots.Add(entity);
            foreach (ProviderCapability capability in snapshot.Capabilities.OrderBy(item => item.CapabilityId, StringComparer.Ordinal))
            {
                context.ProviderCapabilityEntries.Add(new ProviderCapabilityEntryEntity
                {
                    SnapshotId = entity.Id,
                    CapabilityId = capability.CapabilityId,
                    State = (int)capability.State,
                    LifecycleState = (int)capability.LifecycleState,
                    DisabledReason = capability.DisabledReason,
                });
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return candidate;
        }
    }

    public async Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(
        string providerStableId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredProviderCapabilitySnapshot> items = await ListRecentAsync(
            providerStableId,
            accountId,
            maximumCount: 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return items.Count == 0 ? null : items[0];
    }

    public async Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(
        string providerStableId,
        string accountId,
        int maximumCount = 20,
        CancellationToken cancellationToken = default)
    {
        ProviderAccountReference lookup = new(providerStableId, accountId, "Lookup", null);
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), "Capability history requests must contain 1-100 snapshots.");
        }

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            List<ProviderCapabilitySnapshotEntity> snapshots = await context.ProviderCapabilitySnapshots
                .AsNoTracking()
                .Where(item => item.ProviderStableId == lookup.ProviderStableId && item.AccountId == lookup.AccountId)
                .OrderByDescending(item => item.CapturedAtUnixMilliseconds)
                .ThenByDescending(item => item.Id)
                .Take(maximumCount)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshots.Count == 0)
            {
                return [];
            }

            Guid[] snapshotIds = snapshots.Select(item => item.Id).ToArray();
            List<ProviderCapabilityEntryEntity> entries = await context.ProviderCapabilityEntries
                .AsNoTracking()
                .Where(item => snapshotIds.Contains(item.SnapshotId))
                .OrderBy(item => item.CapabilityId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            ILookup<Guid, ProviderCapabilityEntryEntity> bySnapshot = entries.ToLookup(item => item.SnapshotId);
            return snapshots.Select(item => Map(item, bySnapshot[item.Id])).ToArray();
        }
    }

    private static StoredProviderCapabilitySnapshot Map(
        ProviderCapabilitySnapshotEntity entity,
        IEnumerable<ProviderCapabilityEntryEntity> entries)
    {
        ProviderAccountReference account = new(
            entity.ProviderStableId,
            entity.AccountId,
            entity.AccountDisplayName,
            entity.CredentialTargetName is null ? null : new CredentialReference(entity.CredentialTargetName),
            entity.EndpointId,
            entity.RegionId);
        ProviderCapabilitySnapshot snapshot = new(
            account,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.CapturedAtUnixMilliseconds),
            entity.ProvenanceId,
            entries.Select(item => new ProviderCapability(
                item.CapabilityId,
                (ProviderCapabilityState)item.State,
                (ProviderLifecycleState)item.LifecycleState,
                item.DisabledReason)));
        return new StoredProviderCapabilitySnapshot(
            entity.Id,
            snapshot,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.ExpiresAtUnixMilliseconds));
    }
}

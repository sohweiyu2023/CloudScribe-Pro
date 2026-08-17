using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Providers;

public sealed class EfProviderAccountStore(
    IDbContextFactory<CloudScribeDbContext> contextFactory,
    TimeProvider timeProvider) : IProviderAccountStore
{
    public async Task<ProviderAccountSnapshot> CreateAsync(
        ProviderAccountReference account,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            bool exists = await context.ProviderAccounts
                .AsNoTracking()
                .AnyAsync(item => item.ProviderStableId == account.ProviderStableId && item.AccountId == account.AccountId, cancellationToken)
                .ConfigureAwait(false);
            if (exists)
            {
                throw new InvalidOperationException("Provider account already exists; explicit revision-bound update is required.");
            }

            long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            ProviderAccountEntity entity = CreateEntity(account, isEnabled, now);
            context.ProviderAccounts.Add(entity);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Map(entity);
        }
    }

    public async Task<ProviderAccountSnapshot> UpdateAsync(
        ProviderAccountReference account,
        bool isEnabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);

        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            ProviderAccountEntity? entity = await context.ProviderAccounts
                .SingleOrDefaultAsync(item => item.ProviderStableId == account.ProviderStableId && item.AccountId == account.AccountId, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                throw new KeyNotFoundException("Provider account does not exist.");
            }
            if (entity.Revision != expectedRevision)
            {
                throw new InvalidOperationException("Provider account changed since it was inspected; reload before updating.");
            }

            entity.DisplayName = account.DisplayName;
            entity.CredentialTargetName = account.CredentialReference?.TargetName;
            entity.EndpointId = account.EndpointId;
            entity.RegionId = account.RegionId;
            entity.IsEnabled = isEnabled;
            entity.Revision++;
            entity.UpdatedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new InvalidOperationException("Provider account changed concurrently; reload before updating.", exception);
            }
            return Map(entity);
        }
    }

    public async Task<ProviderAccountSnapshot?> FindAsync(
        string providerStableId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ProviderAccountReference lookup = new(providerStableId, accountId, "Lookup", null);
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            ProviderAccountEntity? entity = await context.ProviderAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProviderStableId == lookup.ProviderStableId && item.AccountId == lookup.AccountId, cancellationToken)
                .ConfigureAwait(false);
            return entity is null ? null : Map(entity);
        }
    }

    public async Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        CloudScribeDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            List<ProviderAccountEntity> entities = await context.ProviderAccounts
                .AsNoTracking()
                .OrderBy(item => item.ProviderStableId)
                .ThenBy(item => item.AccountId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(Map).ToArray();
        }
    }

    private static ProviderAccountEntity CreateEntity(ProviderAccountReference account, bool isEnabled, long now) => new()
    {
        ProviderStableId = account.ProviderStableId,
        AccountId = account.AccountId,
        DisplayName = account.DisplayName,
        CredentialTargetName = account.CredentialReference?.TargetName,
        EndpointId = account.EndpointId,
        RegionId = account.RegionId,
        IsEnabled = isEnabled,
        Revision = 1,
        CreatedAtUnixMilliseconds = now,
        UpdatedAtUnixMilliseconds = now,
    };

    private static ProviderAccountSnapshot Map(ProviderAccountEntity entity) => new(
        new ProviderAccountReference(
            entity.ProviderStableId,
            entity.AccountId,
            entity.DisplayName,
            entity.CredentialTargetName is null ? null : new CredentialReference(entity.CredentialTargetName),
            entity.EndpointId,
            entity.RegionId),
        entity.IsEnabled,
        entity.Revision,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMilliseconds));
}

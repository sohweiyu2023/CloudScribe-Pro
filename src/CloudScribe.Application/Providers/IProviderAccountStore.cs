using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Providers;

public interface IProviderAccountStore
{
    Task<ProviderAccountSnapshot> CreateAsync(
        ProviderAccountReference account,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<ProviderAccountSnapshot> UpdateAsync(
        ProviderAccountReference account,
        bool isEnabled,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ProviderAccountSnapshot?> FindAsync(
        string providerStableId,
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}

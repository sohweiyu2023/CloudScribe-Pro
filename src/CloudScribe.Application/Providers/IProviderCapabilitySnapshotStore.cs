using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Providers;

public interface IProviderCapabilitySnapshotStore
{
    Task<StoredProviderCapabilitySnapshot> SaveAsync(
        ProviderCapabilitySnapshot snapshot,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(
        string providerStableId,
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(
        string providerStableId,
        string accountId,
        int maximumCount = 20,
        CancellationToken cancellationToken = default);
}

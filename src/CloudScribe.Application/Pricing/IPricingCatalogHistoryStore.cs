using CloudScribe.Domain.Pricing;

namespace CloudScribe.Application.Pricing;

public interface IPricingCatalogHistoryStore
{
    Task<PricingCatalogSnapshot> SaveSnapshotAsync(
        ReadOnlyMemory<byte> utf8Catalog,
        PricingCatalogTrustState trustState,
        PricingCatalogSource source,
        string? signatureKeyId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PricingCatalogSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default);

    Task<PricingCatalogSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PricingCatalogActivation>> ListActivationsAsync(CancellationToken cancellationToken = default);

    Task<PricingCatalogActivation> ActivateAsync(
        PricingCatalogActivationRequest request,
        CancellationToken cancellationToken = default);
}

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only, independently installed additions. A damaged catalog must fail closed, never return a partial family set.</summary>
public interface IChirpArticleAdmissionCatalog : IChirpArticleAdmissionStore
{
    Task<ChirpArticleAdmissionCatalog> LoadCatalogVerifiedAsync(CancellationToken cancellationToken = default);
}

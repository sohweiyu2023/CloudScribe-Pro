namespace CloudScribe.Infrastructure.Generation;

/// <summary>Adapter to the existing compiler, approved submission and complete-coverage export chain. Never an authority source.</summary>
public interface IChirpWholeArticleRuntime
{
    bool RequiresCatalogRefresh => false;
    Task RefreshCatalogAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Catalog refresh is not available in this runtime.");
    Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken);
    Task GenerateRecoveryAsync(ChirpWholeArticleJob job, ChirpArticleRecoveryChild child, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Bounded child generation is unavailable in this runtime.");
    Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken);
}

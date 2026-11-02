namespace CloudScribe.Infrastructure.Generation;

/// <summary>Independent admission of the article-level workflow, never inferred from request-level permission.</summary>
public sealed record ChirpWholeArticleGrant(Guid GrantId, IReadOnlyList<string> PlanSha256s, int MaximumCatalogRefreshes = 0);

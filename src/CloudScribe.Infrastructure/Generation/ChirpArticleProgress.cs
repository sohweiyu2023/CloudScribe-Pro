namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpArticleProgress(Guid DocumentId, string PlanSha256, int SavedCoverageParts,
    int TotalCoverageParts, bool Complete, IReadOnlyList<ChirpPartProgress> Parts);

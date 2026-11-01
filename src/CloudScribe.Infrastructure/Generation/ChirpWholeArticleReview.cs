namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpWholeArticleReview(Guid PreviewId, string ReviewSha256, Guid DocumentId,
    string PlanSha256, string VoiceName, string Currency, int Scale, int AcceptedParts, int TotalParts,
    int RemainingRequests, long MaximumAdditionalMinorUnits, long RetainedCommitmentsMinorUnits,
    DateTimeOffset ExpiresAtUtc, int MaximumCatalogRefreshes, DateTimeOffset ConfirmByUtc);

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Separately admitted bounded additional reservations; terminal errors never create this permission.</summary>
public sealed record ChirpWholeArticleRecoveryGrant(int Version, Guid GrantId, string PlannerVersion,
    int MaximumDepth, int MaximumChildAttemptsPerRange, int MaximumChildAttemptsPerJob,
    int MaximumChildAttemptsTotal, long MaximumAdditionalMinorUnits,
    IReadOnlyList<ChirpReviewedSentenceRejection> ReviewedHistoricalRejections);

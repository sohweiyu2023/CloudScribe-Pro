namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpRequestReview(Guid PreviewId, string ReviewSha256, string PlanSha256, int CoverageOrdinal,
    Guid DocumentId, Guid RevisionId, string PayloadSha256, int PayloadBytes, string Currency, int Scale,
    long EstimateMinorUnits, long MaximumMinorUnits, long ProjectCommittedMinorUnits, long AllocationRemainingMinorUnits,
    Guid? ExistingAuthorizationId, DateTimeOffset ExpiresAtUtc, string PricingProvenanceId, string VoiceName,
    Guid? FailedParentAuthorizationId = null, bool IsCatalogRevalidation = false);

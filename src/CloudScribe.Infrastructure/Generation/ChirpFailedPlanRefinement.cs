namespace CloudScribe.Infrastructure.Generation;

/// <summary>Immutable predecessor and exact terminal failed attempt. These pins require independent database verification.</summary>
public sealed record ChirpFailedPlanRefinement(ChirpArticlePlanProposal Parent, int FailedCoverageOrdinal,
    Guid FailedAuthorizationId, string FailedAttemptRowSha256, string FailedQueueRowSha256, string FailedLocalAttemptId);

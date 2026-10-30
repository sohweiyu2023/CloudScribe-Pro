namespace CloudScribe.Infrastructure.Generation;

/// <summary>Independent one-attempt authority; never inferred from diagnostics or a caller's assertion.</summary>
public sealed record ChirpArticleSuccessorGrant(Guid GrantId, Guid RootAuthorizationId, Guid ParentAuthorizationId,
    string PlanSha256, int CoverageOrdinal, string ParentKey, string ParentAttemptRowSha256, string ParentQueueRowSha256,
    string ParentLocalAttemptId, string CredentialReferenceId, string ReviewedFailureEvidenceSha256,
    int MaximumNewAttempts, long MaximumAdditionalReservationMinorUnits);

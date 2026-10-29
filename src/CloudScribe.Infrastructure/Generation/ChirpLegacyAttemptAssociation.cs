namespace CloudScribe.Infrastructure.Generation;

/// <summary>Separately reviewed historical association, never reconstructed wire evidence or new spend authority.</summary>
public sealed record ChirpLegacyAttemptAssociation(string PlanSha256, Guid DocumentId, Guid RevisionId,
    int StartScalar, int EndScalarExclusive, string TextSha256, string AccountId, string ProjectId, string OperationId,
    string IdempotencyKey, string QueueRowSha256, Guid SpendAuthorizationId, string SpendRowSha256,
    string OriginalPayloadSha256, int OriginalPayloadBytes, long RetainedReservationMinorUnits, string ReviewedEvidenceSha256);

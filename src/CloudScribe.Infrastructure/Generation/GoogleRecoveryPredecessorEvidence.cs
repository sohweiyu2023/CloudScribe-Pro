namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryPredecessorEvidence(string LogicalNarrationId, string AccountId, string OperationId,
    string HistoricalKey, string HistoricalStateSha256, string ProjectId, Guid DocumentId, Guid RevisionId,
    string TextSha256, string OriginalPayloadSha256, string AdmissionSha256, DateTimeOffset ExpiresAtUtc);

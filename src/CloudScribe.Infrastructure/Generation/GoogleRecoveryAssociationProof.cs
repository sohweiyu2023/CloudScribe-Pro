namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryAssociationProof(string LogicalNarrationId, string RootHistoricalKey,
    string RootHistoricalStateSha256, string AdmissionSha256);

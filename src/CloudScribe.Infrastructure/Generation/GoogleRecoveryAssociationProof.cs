namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryAssociationProof(string LogicalNarrationId, string RootHistoricalKey,
    string RootHistoricalStateSha256, string AdmissionSha256,
    int MaximumNewAttempts = 0, long MaximumAdditionalReservationMinorUnits = 0);

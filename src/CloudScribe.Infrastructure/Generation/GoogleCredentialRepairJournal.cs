namespace CloudScribe.Infrastructure.Generation;

internal sealed record GoogleCredentialRepairJournal(Guid RepairId, string AccountId, string ExpectedAccountJson,
    string FinancialSha256, GoogleCredentialRepairIdentity Identity, string StagedReference, string Phase, string ApprovalSha256);

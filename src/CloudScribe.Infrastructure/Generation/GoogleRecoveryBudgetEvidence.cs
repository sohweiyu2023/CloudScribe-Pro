namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryBudgetEvidence(string AccountId, string ProjectId, string Currency, int Scale,
    string FinancialStateSha256, GoogleRecoveryBudgetReview ApprovedBudget, DateTimeOffset ExpiresAtUtc);

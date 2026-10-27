namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryExecutionAuthorization(GoogleRecoveryApprovedAttempt Approval,
    string ProjectId, GoogleGenerationSpendAuthorization SpendAuthorization, string FinancialStateSha256,
    GoogleRecoveryBudgetReview? Budget = null);

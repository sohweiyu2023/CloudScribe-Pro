namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private independently reviewed installer input, excluded from clean packages and public source.</summary>
public sealed record ChirpArticleAdmissionPolicy(int SchemaVersion, string AppSha256, string InfrastructureSha256,
    string ProfileRootSha256, string AccountId, string ProjectId, string Currency, int Scale,
    DateTimeOffset ExpiresAtUtc, string SourceApprovalSha256, RecoveryBudgetBaseline Budget,
    IReadOnlyList<RecoveryFinancialRowPin> ScopeRows, IReadOnlyList<RecoveryFinancialRowPin> PriorArticleAttemptRows,
    IReadOnlyList<AdmittedChirpArticle> Articles, int MaximumNewAttempts, long MaximumAdditionalReservationMinorUnits);

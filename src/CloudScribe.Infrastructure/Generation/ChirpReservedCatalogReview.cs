namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only candidate for independent review. Neither this record nor its digest authorizes execution.</summary>
public sealed record ChirpReservedCatalogReview(Guid AuthorizationId, string IdempotencyKey,
    string OriginalAttemptSha256, string OriginalQueueSha256, string RetainedFinancialSha256,
    GoogleGenerationSubmissionEnvelope OriginalEnvelope, GoogleGenerationSubmissionEnvelope CandidateEnvelope,
    Guid CandidateCatalogId, DateTimeOffset CandidateCatalogExpiresUtc, string ReviewSha256)
{
    public bool GrantsDispatchAuthority { get; }
    public long AdditionalReservationMinorUnits { get; }
    public bool CandidateCatalogEvidenceAuthenticated { get; }
    public bool IndependentFinancialBaselineReconciled { get; }
}

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private independently pinned resume authority. It cannot create a reservation or a descendant.</summary>
public sealed record ChirpReservationResumePolicy(int SchemaVersion, Guid MappingId, string AppSha256,
    string InfrastructureSha256, string ProfileRootSha256, string SourceApprovalSha256, DateTimeOffset ExpiresAtUtc,
    ChirpArticleAdmissionPolicy HistoricalPolicy, Guid AuthorizationId, string OriginalAttemptRowSha256,
    string CleanQueueRowSha256, string FinancialSha256, DateTimeOffset OriginalApprovedAtUtc, GoogleGenerationSubmissionEnvelope OriginalEnvelope,
    GoogleGenerationSubmissionEnvelope ExecutionEnvelope, Guid CatalogSnapshotId, DateTimeOffset CatalogExpiresAtUtc,
    string ProviderEvidenceSha256, string SelectionSha256, string RuntimeEvidenceSha256, string RequestIdentitySha256,
    int MaximumExistingDispatches, int MaximumNewReservations, long MaximumAdditionalReservationMinorUnits,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? PriorMappingSha256 = null);

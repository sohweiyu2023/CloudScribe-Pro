using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Private review material only: no source approval, installer pin or dispatch authority is created here.</summary>
public sealed record ChirpReservedResumeDraft(ChirpArticleAdmissionPolicy HistoricalPolicy, ChirpReservationResumeInspection Retained,
    GoogleGenerationSubmissionEnvelope CandidateEnvelope, Guid CatalogSnapshotId, DateTimeOffset CatalogExpiresAtUtc,
    string SelectionSha256, string RuntimeEvidenceSha256, string RequestIdentitySha256, string? PriorMappingSha256)
{
    public bool GrantsAuthority { get; }
}

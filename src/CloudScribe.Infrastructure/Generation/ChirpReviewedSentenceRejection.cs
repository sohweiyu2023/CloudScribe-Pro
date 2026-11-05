namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit independent review of retained protected evidence, never represented as a native historical observation.</summary>
public sealed record ChirpReviewedSentenceRejection(Guid AuthorizationId, string AttemptRowSha256,
    string QueueRowSha256, string PayloadSha256, int PayloadBytes, Guid ProtectedRecordId,
    string ProtectedRecordSha256, string ReviewedEvidenceSha256);

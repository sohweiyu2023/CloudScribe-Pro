namespace CloudScribe.Application.Generation;

/// <summary>Local output provenance, never permission to submit a provider request.</summary>
public sealed record ArticleAcceptedAudio(
    Guid DocumentId, Guid RevisionId, string TextSha256, string AccountId,
    string OperationId, string IdempotencyKey, string LocalAttemptId,
    string PayloadSha256, string AudioPath, long AudioBytes, string AudioSha256,
    string VoiceName, string LanguageCode,
    string? HistoricalEvidenceSha256 = null, string? HistoricalAnchorSha256 = null);

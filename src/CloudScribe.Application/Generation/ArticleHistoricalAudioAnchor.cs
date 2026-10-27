namespace CloudScribe.Application.Generation;

/// <summary>Reviewed historical evidence, never a provider response or spend authorization.</summary>
public sealed record ArticleHistoricalAudioAnchor(
    Guid ParentId, Guid ParentRevisionId, string ParentSha256, int Ordinal,
    Guid DocumentId, Guid RevisionId, string TextSha256,
    string AccountId, string OperationId, string IdempotencyKey, string LocalAttemptId,
    string PayloadSha256, int PayloadBytes, string AudioSha256, long AudioBytes,
    string VoiceName, string LanguageCode, string EvidenceSha256);

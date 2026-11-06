namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleLongAudioArticleOutput(int Version, Guid DocumentId, Guid RevisionId, string SourceSha256,
    string RootBindingSha256, IReadOnlyList<GoogleLongAudioRecoveryOutput> Ordered, string Mp3Path, long Bytes, string Sha256);

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Local verified-file provenance, separate from synchronous accepted chunks and invoice settlement.</summary>
public sealed record GoogleLongAudioDocumentReceipt(int Version, Guid DocumentId, Guid RevisionId,
    string SourceSha256, string RequestBindingSha256, DateTimeOffset VerifiedUtc, GoogleLongAudioVerifiedArtifact Artifact);

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private immutable installation input; no text or credential material is embedded.</summary>
public sealed record GoogleLongAudioDocumentJob(Guid DocumentId, Guid RevisionId, string SourceSha256,
    Guid AttemptId, string OutputGcsUri, string InputMode, long MaximumOutputBytes, int ExpectedSampleRate, int ExpectedChannels);

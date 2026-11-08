namespace CloudScribe.Application.Batch;

/// <summary>Original saved text behind a narration copy. Raw TXT bytes remain pinned separately in SourceSha256.</summary>
public sealed record BatchNarrationSource(Guid DocumentId, Guid RevisionId, string TextSha256, string TransformVersion, int OmittedUrlCount);

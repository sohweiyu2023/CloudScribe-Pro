namespace CloudScribe.Application.Documents;

public sealed record NarrationCopyPreparation(
    Guid SourceDocumentId,
    Guid SourceRevisionId,
    string SourceTextSha256,
    DocumentSnapshot Narration,
    int OmittedUrlCount,
    string TransformVersion);

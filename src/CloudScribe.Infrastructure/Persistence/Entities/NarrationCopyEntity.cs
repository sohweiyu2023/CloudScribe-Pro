namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class NarrationCopyEntity
{
    public Guid SourceDocumentId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public required string SourceTextSha256 { get; set; }
    public string? SourceContentRelativePath { get; set; }
    public long? SourceContentByteLength { get; set; }
    public required string TransformVersion { get; set; }
    public required string TransformSettings { get; set; }
    public Guid ChildDocumentId { get; set; }
    public Guid ChildRevisionId { get; set; }
    public required string ChildTextSha256 { get; set; }
    public int OmittedUrlCount { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
}

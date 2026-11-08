namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class BatchSourceImportEntity
{
    public Guid BatchId { get; set; }
    public Guid ItemId { get; set; }
    public required string RawSha256 { get; set; }
    public required string SourceTitle { get; set; }
    public Guid SourceDocumentId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public required string SourceTextSha256 { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
}

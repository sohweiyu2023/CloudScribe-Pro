namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class NarrationPartGroupEntity
{
    public Guid Id { get; set; }
    public Guid ParentDocumentId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public required string SourceSha256 { get; set; }
    public required string PartitionSha256 { get; set; }
    public required string OrderedPartHashes { get; set; }
    public int ExpectedPartCount { get; set; }
    public bool Complete { get; set; }
}


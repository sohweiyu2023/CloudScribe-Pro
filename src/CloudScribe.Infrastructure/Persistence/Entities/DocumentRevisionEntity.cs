namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class DocumentRevisionEntity
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public long CreatedAtUnixMilliseconds { get; set; }

    public int RevisionKind { get; set; }

    public string? Name { get; set; }

    public required string ContentText { get; set; }

    public required string ContentSha256 { get; set; }

    public string? ContentRelativePath { get; set; }

    public long? ContentByteLength { get; set; }

    public string? ImportProvenance { get; set; }
}

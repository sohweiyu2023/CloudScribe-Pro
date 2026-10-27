namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class NarrationPartLinkEntity
{
    public Guid GroupId { get; set; }
    public Guid ChildDocumentId { get; set; }
    public Guid ChildRevisionId { get; set; }
    public required string ChildSha256 { get; set; }
    public int Ordinal { get; set; }
}

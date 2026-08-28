namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class DocumentTagEntity
{
    public Guid DocumentId { get; set; }

    public Guid TagId { get; set; }
}

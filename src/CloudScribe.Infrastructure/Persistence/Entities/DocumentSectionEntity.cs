namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class DocumentSectionEntity
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public int Ordinal { get; set; }

    public required string Title { get; set; }

    public long StartGraphemeOffset { get; set; }

    public long? EndGraphemeOffset { get; set; }
}

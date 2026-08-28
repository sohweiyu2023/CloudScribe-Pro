namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class ReadingPositionEntity
{
    public Guid DocumentId { get; set; }

    public long GraphemeOffset { get; set; }

    public Guid? ActiveSectionId { get; set; }

    public long UpdatedAtUnixMilliseconds { get; set; }
}

namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class BookmarkEntity
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public required string Name { get; set; }

    public long GraphemeOffset { get; set; }

    public long CreatedAtUnixMilliseconds { get; set; }
}

namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class TagEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }
}

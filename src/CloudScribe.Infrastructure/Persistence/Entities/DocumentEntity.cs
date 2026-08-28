namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class DocumentEntity
{
    public Guid Id { get; set; }

    public required string Title { get; set; }

    public required string DraftText { get; set; }

    public long CreatedAtUnixMilliseconds { get; set; }

    public long UpdatedAtUnixMilliseconds { get; set; }

    public int Status { get; set; }

    public bool IsFavorite { get; set; }

    public Guid? CurrentRevisionId { get; set; }

    public string? VoiceReference { get; set; }

    public string? PresetReference { get; set; }

    public long ConcurrencyVersion { get; set; }
}

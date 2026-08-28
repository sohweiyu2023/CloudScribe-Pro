namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class ActivityTimelineEntity
{
    public Guid Id { get; set; }

    public long OccurredAtUnixMilliseconds { get; set; }

    public int Severity { get; set; }

    public required string EventCode { get; set; }

    public required string Summary { get; set; }

    public required string CorrelationId { get; set; }
}

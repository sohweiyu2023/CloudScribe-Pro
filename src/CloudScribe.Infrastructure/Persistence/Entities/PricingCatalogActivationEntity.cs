namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class PricingCatalogActivationEntity
{
    public long Sequence { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid? PreviousSnapshotId { get; set; }
    public int Kind { get; set; }
    public int ApprovalKind { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long OccurredAtUnixMilliseconds { get; set; }
}

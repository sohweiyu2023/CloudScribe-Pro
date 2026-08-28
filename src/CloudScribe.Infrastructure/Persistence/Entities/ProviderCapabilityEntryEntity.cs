namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class ProviderCapabilityEntryEntity
{
    public Guid SnapshotId { get; set; }
    public string CapabilityId { get; set; } = string.Empty;
    public int State { get; set; }
    public int LifecycleState { get; set; }
    public string? DisabledReason { get; set; }
}

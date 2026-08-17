namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class ProviderCapabilitySnapshotEntity
{
    public Guid Id { get; set; }
    public string ProviderStableId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string AccountDisplayName { get; set; } = string.Empty;
    public string? CredentialTargetName { get; set; }
    public string? EndpointId { get; set; }
    public string? RegionId { get; set; }
    public long CapturedAtUnixMilliseconds { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public string ProvenanceId { get; set; } = string.Empty;
}

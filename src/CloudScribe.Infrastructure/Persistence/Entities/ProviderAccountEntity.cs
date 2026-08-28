namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class ProviderAccountEntity
{
    public string ProviderStableId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CredentialTargetName { get; set; }
    public string? EndpointId { get; set; }
    public string? RegionId { get; set; }
    public bool IsEnabled { get; set; }
    public long Revision { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}

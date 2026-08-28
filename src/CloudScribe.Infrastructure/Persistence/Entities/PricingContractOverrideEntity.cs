namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class PricingContractOverrideEntity
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public byte[] OverrideBytes { get; set; } = [];
    public string Label { get; set; } = string.Empty;
    public string ProvenanceId { get; set; } = string.Empty;
    public long CapturedAtUnixMilliseconds { get; set; }
}

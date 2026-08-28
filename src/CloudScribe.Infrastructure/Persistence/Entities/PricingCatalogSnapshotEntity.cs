namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class PricingCatalogSnapshotEntity
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public byte[] CatalogBytes { get; set; } = [];
    public int TrustState { get; set; }
    public int SourceKind { get; set; }
    public string SourceLabel { get; set; } = string.Empty;
    public long CapturedAtUnixMilliseconds { get; set; }
    public string? SignatureKeyId { get; set; }
}

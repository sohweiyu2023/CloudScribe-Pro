namespace CloudScribe.Infrastructure.Persistence.Entities;

public sealed class GoogleGenerationSpendAuthorizationEntity
{
    public Guid Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string CredentialReferenceId { get; set; } = string.Empty;
    public string CapabilityProvenanceId { get; set; } = string.Empty;
    public string PricingProvenanceId { get; set; } = string.Empty;
    public int RequestRevision { get; set; }
    public string VoiceName { get; set; } = string.Empty;
    public string AudioEncoding { get; set; } = string.Empty;
    public string CompiledPayloadSha256 { get; set; } = string.Empty;
    public int CompiledPayloadBytes { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int Scale { get; set; }
    public long AuthorizedMaximumMinorUnits { get; set; }
    public long ApprovedEstimateMinorUnits { get; set; }
    public long ApprovedAtUnixMilliseconds { get; set; }
}

namespace CloudScribe.Application.Pricing;

public sealed record PricingContractOverrideSnapshot(
    Guid Id,
    string Sha256,
    long ByteLength,
    string Label,
    string ProvenanceId,
    DateTimeOffset CapturedAtUtc)
{
    public static bool AffectsPricing => false;
}

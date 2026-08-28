using CloudScribe.Domain.Pricing;

namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogSnapshot(
    Guid Id,
    string Sha256,
    long ByteLength,
    PricingCatalogTrustState TrustState,
    PricingCatalogSource Source,
    DateTimeOffset CapturedAtUtc,
    string? SignatureKeyId)
{
    public bool RequiresManualApproval => TrustState == PricingCatalogTrustState.ValidUnsigned;
}

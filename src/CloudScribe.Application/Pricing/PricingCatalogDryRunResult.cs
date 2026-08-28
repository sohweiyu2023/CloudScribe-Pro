using CloudScribe.Domain.Pricing;

namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogDryRunResult(
    PricingCatalogTrustState TrustState,
    IReadOnlyList<PricingCatalogDiagnostic> Diagnostics,
    string StatusReason)
{
    public bool CanApprove => TrustState is PricingCatalogTrustState.ValidUnsigned or PricingCatalogTrustState.SignatureVerified;
}

using System.Text.Json;
using CloudScribe.Application.Pricing;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class UnavailablePricingCatalogContractValidator : IPricingCatalogContractValidator
{
    public PricingCatalogContractValidation Validate(JsonElement catalogRoot)
    {
        if (catalogRoot.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Pricing catalog validation requires an object root.", nameof(catalogRoot));
        }

        return new PricingCatalogContractValidation(
            contractAvailable: false,
            [new PricingCatalogDiagnostic(
                "catalog.contract.unavailable",
                "The exact controlling v2.22 pricing schema and seed bytes are not admitted, so catalog approval is blocked.")]);
    }
}

using System.Text.Json;

namespace CloudScribe.Application.Pricing;

public interface IPricingCatalogContractValidator
{
    PricingCatalogContractValidation Validate(JsonElement catalogRoot);
}

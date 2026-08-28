using System.Text.Json;

namespace CloudScribe.Application.Pricing;

public interface IPricingCatalogContractValidator
{
    PricingCatalogContractValidation Validate(JsonElement catalogRoot);

    PricingCatalogContractValidation Validate(ReadOnlyMemory<byte> utf8Catalog, JsonElement catalogRoot)
        => Validate(catalogRoot);
}

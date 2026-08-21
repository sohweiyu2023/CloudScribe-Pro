using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Pricing;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class AdmittedV222PricingCatalogContractValidator(V222ControlSet controls) : IPricingCatalogContractValidator
{
    public PricingCatalogContractValidation Validate(JsonElement catalogRoot)
    {
        if (catalogRoot.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Pricing catalog validation requires an object root.", nameof(catalogRoot));
        }

        return new PricingCatalogContractValidation(
            true,
            [new PricingCatalogDiagnostic(
                "catalog.contract.raw-bytes-required",
                "The admitted v2.22 contract requires the original UTF-8 catalog bytes so authenticated catalog identity cannot be bypassed by JSON reserialization.")]);
    }

    public PricingCatalogContractValidation Validate(ReadOnlyMemory<byte> utf8Catalog, JsonElement catalogRoot)
    {
        if (catalogRoot.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Pricing catalog validation requires an object root.", nameof(catalogRoot));
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(utf8Catalog.Span)).ToLowerInvariant();
        if (!string.Equals(actualSha256, V222ControlSet.PricingSeedSha256, StringComparison.Ordinal))
        {
            return new PricingCatalogContractValidation(
                true,
                [new PricingCatalogDiagnostic(
                    "catalog.contract.unrecognized-bytes",
                    "Catalog bytes are not the authenticated v2.22 seed admitted by this build. A changed catalog must pass a separately authenticated schema/semantic update before approval.")]);
        }

        if (!catalogRoot.TryGetProperty("schema_version", out JsonElement schemaVersion)
            || !string.Equals(schemaVersion.GetString(), "1.1.5", StringComparison.Ordinal)
            || !catalogRoot.TryGetProperty("catalog", out JsonElement catalog)
            || catalog.ValueKind != JsonValueKind.Object
            || !catalog.TryGetProperty("catalog_version", out JsonElement catalogVersion)
            || !string.Equals(catalogVersion.GetString(), V222ControlSet.CatalogVersion, StringComparison.Ordinal))
        {
            return new PricingCatalogContractValidation(
                true,
                [new PricingCatalogDiagnostic(
                    "catalog.contract.identity-shape-mismatch",
                    "Authenticated catalog identity matched but required schema/catalog version fields were not present; approval is blocked.")]);
        }

        _ = controls.PricingSchemaUtf8;
        return PricingCatalogContractValidation.Valid();
    }
}

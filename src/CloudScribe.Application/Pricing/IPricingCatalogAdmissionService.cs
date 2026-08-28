namespace CloudScribe.Application.Pricing;

public interface IPricingCatalogAdmissionService
{
    PricingCatalogDryRunResult DryRun(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogSignature? signature = null);
}

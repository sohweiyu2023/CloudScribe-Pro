namespace CloudScribe.Application.Pricing;

public interface IPricingCatalogSignatureVerifier
{
    PricingCatalogSignatureVerification Verify(ReadOnlyMemory<byte> catalogBytes, PricingCatalogSignature signature);
}

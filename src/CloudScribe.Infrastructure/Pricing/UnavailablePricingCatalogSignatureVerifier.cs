using CloudScribe.Application.Pricing;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class UnavailablePricingCatalogSignatureVerifier : IPricingCatalogSignatureVerifier
{
    public PricingCatalogSignatureVerification Verify(ReadOnlyMemory<byte> catalogBytes, PricingCatalogSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (catalogBytes.IsEmpty)
        {
            throw new ArgumentException("Pricing catalog bytes cannot be empty.", nameof(catalogBytes));
        }

        return PricingCatalogSignatureVerification.Rejected(
            "Ed25519 verification is not configured. Metadata or an embedded key is never accepted as catalog trust.");
    }
}

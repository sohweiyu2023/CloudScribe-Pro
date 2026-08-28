namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogSignatureVerification(bool IsVerified, string Reason)
{
    public static PricingCatalogSignatureVerification Verified() => new(true, "Detached signature verified against an externally trusted key.");

    public static PricingCatalogSignatureVerification Rejected(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason.Trim());
    }
}

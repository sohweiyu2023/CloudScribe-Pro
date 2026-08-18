namespace CloudScribe.Infrastructure.Pricing;

public sealed class PricingCatalogTrustOptions
{
    public const string SectionName = "CloudScribe:PricingCatalogTrust";

    public IDictionary<string, string> TrustedEd25519PublicKeys { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

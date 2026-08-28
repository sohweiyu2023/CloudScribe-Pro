namespace CloudScribe.Domain.Pricing;

public enum PricingCatalogTrustState
{
    ContractUnavailable = 0,
    ValidationFailed = 1,
    ValidUnsigned = 2,
    SignatureInvalid = 3,
    SignatureVerified = 4,
}

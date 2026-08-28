namespace CloudScribe.Infrastructure.Pricing;

public enum PricingCatalogFormatError
{
    Empty = 0,
    TooLarge = 1,
    InvalidJson = 2,
    TopLevelNotObject = 3,
    DuplicateProperty = 4,
}

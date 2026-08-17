namespace CloudScribe.Application.Pricing;

public enum PricingCatalogSourceKind
{
    BuiltInSeed = 0,
    ImportedFile = 1,
    RemoteUpdate = 2,
    RecoveredHistory = 3,
}

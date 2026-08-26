namespace CloudScribe.Application.Generation;

public sealed record VoiceLabAuditionRequest(
    VoiceLabCatalogSelection Selection,
    bool CachePolicyEligible,
    bool ForceFresh,
    bool ExplicitSpendApproved,
    bool PricingCurrent,
    string OutputFormat);

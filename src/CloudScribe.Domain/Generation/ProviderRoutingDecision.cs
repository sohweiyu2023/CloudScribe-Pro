namespace CloudScribe.Domain.Generation;

public sealed record ProviderRoutingDecision(
    ProviderRoute Selected,
    bool UsedFallback,
    string DecisionReason);

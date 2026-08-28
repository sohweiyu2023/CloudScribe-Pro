namespace CloudScribe.Domain.Safety;

public sealed record CacheClearUserDecision(
    bool MayClearUnprotectedEntries,
    string Warning,
    bool ClaimsSecureErase,
    string? EstimatedCostAvoidance);

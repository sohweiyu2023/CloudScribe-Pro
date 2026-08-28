namespace CloudScribe.Domain.Generation;

public sealed record VoiceAuditionSpendDecision(
    bool MaySubmitBillableRequest,
    bool MayReuseCache,
    string Reason);

namespace CloudScribe.Domain.Generation;

public sealed record VoiceAuditionSpendDecision(
    bool MaySubmitBillableRequest,
    bool MayReuseCache,
    string Reason);

public static class VoiceAuditionSpendPolicy
{
    public static VoiceAuditionSpendDecision Evaluate(
        bool cacheHitEligible,
        bool forceFresh,
        bool explicitSpendApproved,
        bool capabilityCurrent,
        bool pricingCurrent)
    {
        if (!capabilityCurrent)
            return new(false, false, "voice-audition-capability-stale");
        if (!pricingCurrent)
            return new(false, false, "voice-audition-pricing-stale");

        if (cacheHitEligible && !forceFresh)
            return new(false, true, "voice-audition-cache-hit");

        if (!explicitSpendApproved)
            return new(false, false, "voice-audition-spend-approval-required");

        return new(true, false, forceFresh
            ? "voice-audition-force-fresh-approved"
            : "voice-audition-cache-miss-approved");
    }
}

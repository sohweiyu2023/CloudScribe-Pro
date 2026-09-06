namespace CloudScribe.Domain.Generation;

public static class VoiceAuditionExecutionGate
{
    public static VoiceAuditionExecutionAuthorization Authorize(
        bool cacheHitEligible,
        bool forceFresh,
        bool explicitSpendApproved,
        bool capabilityCurrent,
        bool pricingCurrent)
    {
        var decision = VoiceAuditionSpendPolicy.Evaluate(
            cacheHitEligible,
            forceFresh,
            explicitSpendApproved,
            capabilityCurrent,
            pricingCurrent);

        if (!decision.MayReuseCache && !decision.MaySubmitBillableRequest)
        {
            throw new InvalidOperationException($"Voice audition is not authorized: {decision.Reason}.");
        }

        return new VoiceAuditionExecutionAuthorization(
            decision.MayReuseCache,
            decision.MaySubmitBillableRequest,
            decision.Reason);
    }
}

namespace CloudScribe.Domain.Safety;

public static class CacheClearUserDecisionPolicy
{
    public static CacheClearUserDecision Create(
        bool userConfirmedClear,
        string? currencyCode = null,
        long? estimatedAvoidedScaledUnits = null,
        int scale = 2)
    {
        string? estimate = null;
        if (estimatedAvoidedScaledUnits is not null)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
                throw new ArgumentException("Currency is required when estimated cost avoidance is displayed.", nameof(currencyCode));
            estimate = CachePrivacyUserPolicy.DescribeEstimatedCostAvoidance(
                currencyCode,
                estimatedAvoidedScaledUnits.Value,
                scale);
        }

        return new CacheClearUserDecision(
            userConfirmedClear,
            CachePrivacyUserPolicy.ClearCacheWarning,
            CachePrivacyUserPolicy.ClaimSecureEraseOnNormalClear,
            estimate);
    }
}

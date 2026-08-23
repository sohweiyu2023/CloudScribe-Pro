namespace CloudScribe.Domain.Safety;

public sealed record CacheExportDecision(bool IncludeCache, string Reason);

public static class CacheExportDecisionPolicy
{
    public static CacheExportDecision Evaluate(
        bool userExplicitlyRequestedCacheExport,
        bool currentPolicyAllowsCacheExport,
        bool containsProtectedEntries,
        bool containsUnresolvedSubmissions)
    {
        if (!userExplicitlyRequestedCacheExport)
            return new(false, "cache-export-not-requested");
        if (!currentPolicyAllowsCacheExport)
            return new(false, "cache-export-policy-denied");
        if (containsProtectedEntries)
            return new(false, "cache-export-protected-entry-present");
        if (containsUnresolvedSubmissions)
            return new(false, "cache-export-unresolved-submission-present");

        return new(true, "cache-export-explicitly-approved");
    }
}

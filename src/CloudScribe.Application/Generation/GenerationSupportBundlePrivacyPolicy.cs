namespace CloudScribe.Application.Generation;

public sealed record GenerationSupportBundlePrivacyDecision(
    bool IncludeCacheMedia,
    bool IncludeCompiledPayload,
    bool IncludeSourceText,
    bool IncludePrivateCacheLookupKey,
    string Reason);

public static class GenerationSupportBundlePrivacyPolicy
{
    public static GenerationSupportBundlePrivacyDecision Evaluate(
        bool userExplicitlyRequestedDiagnosticBundle,
        bool currentPolicyAllowsDiagnostics)
    {
        if (!userExplicitlyRequestedDiagnosticBundle)
            return new(false, false, false, false, "support-bundle-not-requested");
        if (!currentPolicyAllowsDiagnostics)
            return new(false, false, false, false, "support-bundle-policy-denied");

        // v2.23 support bundles must remain metadata-only for generation privacy.
        return new(false, false, false, false, "support-bundle-metadata-only");
    }

    public static void RequireSafe(GenerationSupportBundlePrivacyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IncludeCacheMedia ||
            decision.IncludeCompiledPayload ||
            decision.IncludeSourceText ||
            decision.IncludePrivateCacheLookupKey)
        {
            throw new InvalidOperationException("Generation support bundle would expose private cache or source material.");
        }
    }
}

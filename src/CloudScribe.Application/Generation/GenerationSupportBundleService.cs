namespace CloudScribe.Application.Generation;

public sealed record GenerationSupportBundleMetadata(
    string ApplicationVersion,
    string Platform,
    string DiagnosticCode,
    DateTimeOffset CreatedAtUtc);

public sealed record GenerationSupportBundle(
    GenerationSupportBundleMetadata Metadata,
    GenerationSupportBundlePrivacyDecision PrivacyDecision);

public sealed class GenerationSupportBundleService
{
    public GenerationSupportBundle CreateMetadataOnly(
        bool userExplicitlyRequestedDiagnosticBundle,
        bool currentPolicyAllowsDiagnostics,
        GenerationSupportBundleMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.ApplicationVersion) ||
            string.IsNullOrWhiteSpace(metadata.Platform) ||
            string.IsNullOrWhiteSpace(metadata.DiagnosticCode))
        {
            throw new InvalidOperationException("Generation support-bundle metadata must be complete.");
        }

        var decision = GenerationSupportBundlePrivacyPolicy.Evaluate(
            userExplicitlyRequestedDiagnosticBundle,
            currentPolicyAllowsDiagnostics);
        GenerationSupportBundlePrivacyPolicy.RequireSafe(decision);

        if (decision.Reason != "support-bundle-metadata-only")
            throw new InvalidOperationException($"Generation support bundle is not authorized: {decision.Reason}");

        return new GenerationSupportBundle(metadata, decision);
    }
}

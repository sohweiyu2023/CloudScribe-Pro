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
    private const int MaxApplicationVersionLength = 64;
    private const int MaxPlatformLength = 64;
    private const int MaxDiagnosticCodeLength = 80;

    public GenerationSupportBundle CreateMetadataOnly(
        bool userExplicitlyRequestedDiagnosticBundle,
        bool currentPolicyAllowsDiagnostics,
        GenerationSupportBundleMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        RequireSafeMetadataToken(metadata.ApplicationVersion, nameof(metadata.ApplicationVersion), MaxApplicationVersionLength, allowDot: true);
        RequireSafeMetadataToken(metadata.Platform, nameof(metadata.Platform), MaxPlatformLength, allowDot: true);
        RequireSafeMetadataToken(metadata.DiagnosticCode, nameof(metadata.DiagnosticCode), MaxDiagnosticCodeLength, allowDot: false);

        if (metadata.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Generation support-bundle timestamp must be normalized to UTC.");

        var decision = GenerationSupportBundlePrivacyPolicy.Evaluate(
            userExplicitlyRequestedDiagnosticBundle,
            currentPolicyAllowsDiagnostics);
        GenerationSupportBundlePrivacyPolicy.RequireSafe(decision);

        if (!string.Equals(decision.Reason, "support-bundle-metadata-only", StringComparison.Ordinal))
            throw new InvalidOperationException($"Generation support bundle is not authorized: {decision.Reason}");

        return new GenerationSupportBundle(metadata, decision);
    }

    private static void RequireSafeMetadataToken(string value, string name, int maxLength, bool allowDot)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Generation support-bundle {name} is not a canonical bounded metadata token.");
        }

        foreach (var c in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || (allowDot && c == '.');
            if (!allowed)
                throw new InvalidOperationException($"Generation support-bundle {name} contains unsafe free-form content.");
        }
    }
}

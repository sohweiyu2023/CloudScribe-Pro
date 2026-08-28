namespace CloudScribe.Application.Generation;

public sealed record GenerationSupportBundle(
    GenerationSupportBundleMetadata Metadata,
    GenerationSupportBundlePrivacyDecision PrivacyDecision);

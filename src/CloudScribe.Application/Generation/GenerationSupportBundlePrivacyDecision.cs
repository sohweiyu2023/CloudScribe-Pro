namespace CloudScribe.Application.Generation;

public sealed record GenerationSupportBundlePrivacyDecision(
    bool IncludeCacheMedia,
    bool IncludeCompiledPayload,
    bool IncludeSourceText,
    bool IncludePrivateCacheLookupKey,
    string Reason);

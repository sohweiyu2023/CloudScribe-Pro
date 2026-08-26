namespace CloudScribe.Application.Generation;

public sealed record GenerationSupportBundleMetadata(
    string ApplicationVersion,
    string Platform,
    string DiagnosticCode,
    DateTimeOffset CreatedAtUtc);

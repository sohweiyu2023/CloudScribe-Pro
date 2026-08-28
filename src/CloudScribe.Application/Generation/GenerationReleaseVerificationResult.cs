namespace CloudScribe.Application.Generation;

public sealed record GenerationReleaseVerificationResult(
    bool IsValid,
    string DiagnosticCode,
    string? ObservedOutputSha256);

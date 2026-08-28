namespace CloudScribe.Domain.Generation;

public sealed record ReturnedMediaValidationResult(
    bool IsValid,
    GenerationAudioFormat? DetectedFormat,
    string DiagnosticCode,
    string Reason)
{
    public static ReturnedMediaValidationResult Valid(GenerationAudioFormat format) =>
        new(true, format, "media.valid", "Returned media passed bounded structural validation.");

    public static ReturnedMediaValidationResult Invalid(string code, string reason) =>
        new(false, null, code, reason);
}

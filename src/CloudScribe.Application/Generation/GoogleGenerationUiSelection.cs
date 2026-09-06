namespace CloudScribe.Application.Generation;

public sealed record GoogleGenerationUiSelection(
    string AccountId,
    string ProjectId,
    string VoiceId,
    string ModelId,
    string CapabilityEvidenceId,
    string OutputFormat);

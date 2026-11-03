namespace CloudScribe.Infrastructure.Generation;

/// <summary>Independent evidence-only purpose; does not grant whole-article execution or infer capture consent.</summary>
public sealed record ChirpSentenceDiagnosticAdmission(int Version, string Purpose, string ProposalSha256,
    string SuccessorGrantSha256, string PrivateCaptureConsentSha256, PrivateErrorCaptureMode RequiredCaptureMode);

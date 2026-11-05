namespace CloudScribe.Infrastructure.Generation;

/// <summary>Diagnostic identity only. It conveys no provider or financial authority and contains no input text.</summary>
public sealed record PrivateErrorCaptureBinding(Guid DocumentId, Guid RevisionId, long RequestVersion,
    string AccountId, string ProjectId, string VoiceId, string CredentialReferenceId, string PayloadSha256,
    PrivateErrorCaptureMode CaptureMode = PrivateErrorCaptureMode.SelectedExplanation,
    Guid? ArticleJobId = null, int? ArticleOrdinal = null, DateTimeOffset? NotAfterUtc = null);

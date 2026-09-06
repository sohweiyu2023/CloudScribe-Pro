namespace CloudScribe.Infrastructure.Generation;

public sealed record VoiceLabProjectAuthorizationEvidence(
    string ProviderId,
    string AccountId,
    string ProjectId,
    long AccountRevision,
    string CredentialReferenceId,
    string CapabilityEvidenceId,
    bool ProjectAuthorized,
    bool PrivateVoiceAccessAuthorized,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsCurrent(DateTimeOffset nowUtc) =>
        ProjectAuthorized &&
        nowUtc.Offset == TimeSpan.Zero &&
        CapturedAtUtc.Offset == TimeSpan.Zero &&
        ExpiresAtUtc.Offset == TimeSpan.Zero &&
        CapturedAtUtc <= nowUtc &&
        nowUtc < ExpiresAtUtc;
}

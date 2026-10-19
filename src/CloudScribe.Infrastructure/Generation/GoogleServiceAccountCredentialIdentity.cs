namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleServiceAccountCredentialIdentity(
    string CredentialReferenceId,
    string ProjectId,
    string ClientEmail,
    string PrivateKeyId,
    Uri TokenUri);

namespace CloudScribe.Application.Security;

public sealed record GoogleCredentialRepairPresence(
    CredentialRolePresence Marker,
    CredentialRolePresence Metadata,
    CredentialRolePresence PrivateKey);

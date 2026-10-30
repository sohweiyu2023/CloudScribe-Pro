namespace CloudScribe.Application.Security;

/// <summary>Non-secret observations for an offline proposal, never an authority assertion.</summary>
public sealed record GoogleCredentialRepairScope(
    string AccountId,
    string ProjectId,
    string ExistingCredentialReference,
    long AccountRevision,
    string FullAccountRowSha256,
    string EndpointBindingSha256,
    string ProfileSha256,
    string BuildSha256,
    string OriginalIdentityAnchorSha256);

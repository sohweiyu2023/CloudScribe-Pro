namespace CloudScribe.Application.Security;

/// <summary>
/// An offline, non-executable proposal. It grants no credential-write, network or spend authority.
/// Production admission must independently authenticate all observations and explicit consent.
/// </summary>
public sealed record GoogleCredentialRepairProposal(
    Guid RepairId,
    GoogleCredentialRepairScope Scope,
    string ProposedCredentialReference,
    string CandidateIdentitySha256);

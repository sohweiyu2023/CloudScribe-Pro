namespace CloudScribe.Infrastructure.Generation;

/// <summary>Must originate in independently admitted recovery permission, never be synthesized from an error.</summary>
public sealed record ChirpSentenceSplitGrant(int Version, Guid GrantId, Guid RootAuthorizationId,
    string SourceSha256, string SourceApprovalSha256, int MaximumDepth, int MaximumChildAttempts,
    long MaximumAdditionalMinorUnits);

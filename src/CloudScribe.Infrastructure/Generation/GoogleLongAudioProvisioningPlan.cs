namespace CloudScribe.Infrastructure.Generation;

/// <summary>Sealed initial installation identity. It never stores or resets a mutable paid claim.</summary>
public sealed record GoogleLongAudioProvisioningPlan(int Version, Guid TransactionId, string ScopeSha256,
    string ParentPolicySha256, string ProfileRootSha256, string AppSha256, string InfrastructureSha256,
    string PreviousMarkerSha256, string NewMarkerSha256, string InitialLedgerSha256, DateTimeOffset CreatedUtc);

using System.Text.Json.Serialization;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Installer-admitted new authority, bound to a completed, exhausted prior grant. Never caller approval.</summary>
public sealed record RecoverySuccessorGrant([property: JsonRequired] Guid PriorAuthorizationId,
    [property: JsonRequired] string PriorChildKey, [property: JsonRequired] string PriorStateSha256,
    [property: JsonRequired] string PriorAdmissionSha256, [property: JsonRequired] string FinancialStateSha256,
    [property: JsonRequired] string SourceApprovalSha256, [property: JsonRequired] RecoveryAllocationTransition? AllocationTransition = null);

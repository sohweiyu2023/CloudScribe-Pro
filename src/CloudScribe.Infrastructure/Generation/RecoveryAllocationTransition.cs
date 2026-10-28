using System.Text.Json.Serialization;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit approval to increase only allocation availability, without reducing any retained commitment.</summary>
public sealed record RecoveryAllocationTransition([property: JsonRequired] string PriorScopeSha256,
    [property: JsonRequired] long PriorAllocationCeilingMinorUnits, [property: JsonRequired] long NewAllocationCeilingMinorUnits,
    [property: JsonRequired] long NewRemainingMinorUnits, [property: JsonRequired] string SourceApprovalSha256);

using System.Text.Json.Serialization;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleRecoveryPredecessorEvidence(string LogicalNarrationId, string AccountId, string OperationId,
    string HistoricalKey, string HistoricalStateSha256, string ProjectId, Guid DocumentId, Guid RevisionId,
    string TextSha256, string OriginalPayloadSha256, string AdmissionSha256, DateTimeOffset ExpiresAtUtc,
    [property: JsonRequired] int MaximumNewAttempts = 0,
    [property: JsonRequired] long MaximumAdditionalReservationMinorUnits = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecoverySuccessorGrant? Successor = null);

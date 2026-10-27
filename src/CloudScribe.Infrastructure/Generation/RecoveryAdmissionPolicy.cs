using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private installer-admitted policy. Never part of a clean or public source/binary package.</summary>
public sealed record RecoveryAdmissionPolicy(int SchemaVersion, string AppSha256, string InfrastructureSha256, string ProfileRootSha256,
    string AccountId, string ProjectId, string Currency, int Scale, DateTimeOffset ExpiresAtUtc,
    RecoveryBudgetBaseline Budget, IReadOnlyList<RecoveryHistoricalAdmission> HistoricalAudio,
    IReadOnlyList<GoogleRecoveryPredecessorEvidence> Predecessors);

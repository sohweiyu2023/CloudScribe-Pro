namespace CloudScribe.Infrastructure.Generation;

/// <summary>An independently reviewed conservative total, including every historical uncertain hold.</summary>
public sealed record GoogleRecoveryBudgetReview(long RetainedCommittedMinorUnits, long ProjectCeilingMinorUnits,
    long RemainingAllocationMinorUnits, string EvidenceSha256,
    RecoveryAllocationTransition? AllocationTransition = null);

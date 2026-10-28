namespace CloudScribe.Infrastructure.Generation;

public sealed record RecoveryBudgetBaseline(long ProjectCommittedMinorUnits, long ProjectCeilingMinorUnits,
    long AllocationCommittedMinorUnits, long AllocationCeilingMinorUnits, string ReviewedEvidenceSha256,
    IReadOnlyList<RecoveryFinancialRowPin> QueueRows, IReadOnlyList<RecoveryFinancialRowPin> SpendRows,
    IReadOnlyList<RecoveryFinancialRowPin> RecoveryRows,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RecoveryFinancialRowPin>? TransitionRows = null);

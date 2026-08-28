namespace CloudScribe.Domain.Observability;

public enum BillableLedgerEventKind
{
    EstimateApproved = 0,
    SubmissionPrepared = 1,
    SubmissionAccepted = 2,
    SubmissionAmbiguous = 3,
    UsageReported = 4,
    Reconciled = 5,
    Cancelled = 6,
    Failed = 7,
}

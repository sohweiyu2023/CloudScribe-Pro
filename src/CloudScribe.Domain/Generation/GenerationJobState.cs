namespace CloudScribe.Domain.Generation;

public enum GenerationJobState
{
    Draft,
    Validating,
    Estimating,
    AwaitingApproval,
    Queued,
    Preparing,
    Submitting,
    SubmissionUnknown,
    Running,
    RateLimited,
    RetryWait,
    Paused,
    Cancelling,
    CancelledUnreconciled,
    CancelledReconciled,
    Partial,
    Completed,
    Failed,
    AbandonedRecoverable,
}

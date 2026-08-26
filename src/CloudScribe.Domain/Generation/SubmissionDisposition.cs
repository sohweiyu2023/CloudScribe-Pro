namespace CloudScribe.Domain.Generation;

public enum SubmissionDisposition
{
    NotSubmitted,
    Accepted,
    RejectedSafeToRetry,
    UnknownRequiresReconciliation,
}

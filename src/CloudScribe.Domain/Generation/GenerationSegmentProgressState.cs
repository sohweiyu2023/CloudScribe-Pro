namespace CloudScribe.Domain.Generation;

public enum GenerationSegmentProgressState
{
    Pending,
    Submitting,
    SubmissionUnknown,
    RetryWait,
    Completed,
    Failed,
    Cancelled,
}

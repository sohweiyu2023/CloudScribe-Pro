namespace CloudScribe.Infrastructure.Generation;

public enum CompletionGateFailure
{
    Unknown, Expired, ClockRollback, ClockDiscontinuity, PendingOutcome, Limit, PinMismatch, Storage, Protocol, Stopped,
}

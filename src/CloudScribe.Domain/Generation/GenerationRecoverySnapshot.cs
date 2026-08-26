namespace CloudScribe.Domain.Generation;

public sealed class GenerationRecoverySnapshot
{
    public GenerationRecoverySnapshot(
        Guid jobId,
        GenerationJobState state,
        int attemptCount,
        int priority,
        long revision,
        GenerationSubmissionRecord? lastSubmission,
        long updatedAtUnixMilliseconds)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        if (attemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        JobId = jobId;
        State = state;
        AttemptCount = attemptCount;
        Priority = priority;
        Revision = revision;
        LastSubmission = lastSubmission;
        UpdatedAtUnixMilliseconds = updatedAtUnixMilliseconds;
    }

    public Guid JobId { get; }

    public GenerationJobState State { get; }

    public int AttemptCount { get; }

    public int Priority { get; }

    public long Revision { get; }

    public GenerationSubmissionRecord? LastSubmission { get; }

    public long UpdatedAtUnixMilliseconds { get; }

    public GenerationRecoveryAction DecideRecovery()
    {
        if (State is GenerationJobState.Completed or GenerationJobState.CancelledReconciled)
        {
            return GenerationRecoveryAction.None("Terminal job requires no restart action.");
        }

        if (GenerationJobStateMachine.RequiresReconciliationBeforeAutomaticRetry(State) ||
            LastSubmission?.RequiresReconciliation == true)
        {
            return GenerationRecoveryAction.Reconcile("Ambiguous provider submission must be reconciled before execution resumes.");
        }

        return State switch
        {
            GenerationJobState.Running => GenerationRecoveryAction.Requeue("Process restart interrupted a running job; resume from durable segment state."),
            GenerationJobState.Submitting => GenerationRecoveryAction.Reconcile("Process restart occurred during provider submission; outcome is not safely inferable."),
            GenerationJobState.Cancelling => GenerationRecoveryAction.Reconcile("Cancellation was in flight and requires provider reconciliation."),
            GenerationJobState.RateLimited or GenerationJobState.RetryWait => GenerationRecoveryAction.Requeue("Persisted retry state may resume through the scheduler."),
            GenerationJobState.Queued or GenerationJobState.Preparing or GenerationJobState.Paused or GenerationJobState.AbandonedRecoverable =>
                GenerationRecoveryAction.Requeue("Durable nonterminal job may return to scheduling."),
            _ => GenerationRecoveryAction.None("State requires explicit user or coordinator action."),
        };
    }
}

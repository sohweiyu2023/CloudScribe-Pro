namespace CloudScribe.Domain.Generation;

public sealed record GenerationRecoverySnapshot(
    Guid JobId,
    GenerationJobState State,
    int AttemptCount,
    int Priority,
    long Revision,
    GenerationSubmissionRecord? LastSubmission,
    long UpdatedAtUnixMilliseconds)
{
    public GenerationRecoverySnapshot
    {
        if (JobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(JobId));
        }

        if (AttemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptCount));
        }

        if (Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }
    }

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

public enum GenerationRecoveryKind
{
    None,
    Requeue,
    Reconcile,
}

public sealed record GenerationRecoveryAction(GenerationRecoveryKind Kind, string Reason)
{
    public static GenerationRecoveryAction None(string reason) => new(GenerationRecoveryKind.None, reason);

    public static GenerationRecoveryAction Requeue(string reason) => new(GenerationRecoveryKind.Requeue, reason);

    public static GenerationRecoveryAction Reconcile(string reason) => new(GenerationRecoveryKind.Reconcile, reason);
}

public sealed class GenerationConcurrencyGate
{
    private readonly int _maximumConcurrent;
    private int _active;

    public GenerationConcurrencyGate(int maximumConcurrent)
    {
        if (maximumConcurrent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrent));
        }

        _maximumConcurrent = maximumConcurrent;
    }

    public int ActiveCount => Volatile.Read(ref _active);

    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= _maximumConcurrent)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _active);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _active, 0);
            throw new InvalidOperationException("Generation concurrency gate released without a matching acquisition.");
        }
    }
}

namespace CloudScribe.Domain.Generation;

public static class GenerationJobStateMachine
{
    private static readonly IReadOnlyDictionary<GenerationJobState, HashSet<GenerationJobState>> Allowed =
        new Dictionary<GenerationJobState, HashSet<GenerationJobState>>
        {
            [GenerationJobState.Draft] = [GenerationJobState.Validating, GenerationJobState.Failed],
            [GenerationJobState.Validating] = [GenerationJobState.Estimating, GenerationJobState.Failed],
            [GenerationJobState.Estimating] = [GenerationJobState.AwaitingApproval, GenerationJobState.Failed],
            [GenerationJobState.AwaitingApproval] = [GenerationJobState.Queued, GenerationJobState.Draft, GenerationJobState.Failed],
            [GenerationJobState.Queued] = [GenerationJobState.Preparing, GenerationJobState.Paused, GenerationJobState.Cancelling, GenerationJobState.Failed],
            [GenerationJobState.Preparing] = [GenerationJobState.Submitting, GenerationJobState.Paused, GenerationJobState.Cancelling, GenerationJobState.Failed],
            [GenerationJobState.Submitting] = [GenerationJobState.Running, GenerationJobState.SubmissionUnknown, GenerationJobState.RateLimited, GenerationJobState.RetryWait, GenerationJobState.Cancelling, GenerationJobState.Failed],
            [GenerationJobState.SubmissionUnknown] = [GenerationJobState.Running, GenerationJobState.CancelledUnreconciled, GenerationJobState.Failed],
            [GenerationJobState.Running] = [GenerationJobState.Completed, GenerationJobState.Partial, GenerationJobState.RateLimited, GenerationJobState.RetryWait, GenerationJobState.Paused, GenerationJobState.Cancelling, GenerationJobState.Failed, GenerationJobState.AbandonedRecoverable],
            [GenerationJobState.RateLimited] = [GenerationJobState.RetryWait, GenerationJobState.Paused, GenerationJobState.Cancelling, GenerationJobState.Failed],
            [GenerationJobState.RetryWait] = [GenerationJobState.Preparing, GenerationJobState.Submitting, GenerationJobState.Paused, GenerationJobState.Cancelling, GenerationJobState.Failed],
            [GenerationJobState.Paused] = [GenerationJobState.Queued, GenerationJobState.Preparing, GenerationJobState.RetryWait, GenerationJobState.Cancelling],
            [GenerationJobState.Cancelling] = [GenerationJobState.CancelledUnreconciled, GenerationJobState.CancelledReconciled, GenerationJobState.Completed, GenerationJobState.Partial, GenerationJobState.Failed],
            [GenerationJobState.CancelledUnreconciled] = [GenerationJobState.CancelledReconciled, GenerationJobState.Failed],
            [GenerationJobState.CancelledReconciled] = [],
            [GenerationJobState.Partial] = [GenerationJobState.Completed, GenerationJobState.Failed],
            [GenerationJobState.Completed] = [],
            [GenerationJobState.Failed] = [GenerationJobState.AbandonedRecoverable],
            [GenerationJobState.AbandonedRecoverable] = [GenerationJobState.Queued, GenerationJobState.Failed],
        };

    public static bool CanTransition(GenerationJobState from, GenerationJobState to)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to))
        {
            return false;
        }

        return Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    public static void EnsureTransition(GenerationJobState from, GenerationJobState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Impossible generation-job transition: {from} -> {to}.");
        }
    }

    public static bool IsTerminal(GenerationJobState state) => state is GenerationJobState.Completed or GenerationJobState.CancelledReconciled;

    public static bool RequiresReconciliationBeforeAutomaticRetry(GenerationJobState state) =>
        state is GenerationJobState.SubmissionUnknown or GenerationJobState.CancelledUnreconciled;
}

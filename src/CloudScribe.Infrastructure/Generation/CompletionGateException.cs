namespace CloudScribe.Infrastructure.Generation;

public sealed class CompletionGateException(CompletionGateFailure reason)
    : InvalidOperationException($"Completion workflow stopped: {reason}. Reconcile the private register; no automatic retry.")
{
    public CompletionGateFailure Reason { get; } = reason;
}

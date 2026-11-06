namespace CloudScribe.Infrastructure.Generation;

/// <summary>One native job owner. Capture preparation is internal to the consented job, not a per-poll user prompt.</summary>
public sealed class GoogleLongAudioRuntimeSession : IDisposable
{
    private readonly GoogleLongAudioRequest request;
    private readonly GoogleLongAudioJob job;
    private readonly GoogleLongAudioProtectedResponseObserver observer;
    private readonly GoogleLongAudioAdapter adapter;
    private readonly GoogleLongAudioOutputBoundary output;
    private readonly Func<CancellationToken, ValueTask> requireInstalled;
    private readonly SemaphoreSlim running = new(1, 1);
    private bool disposed;

    internal GoogleLongAudioRuntimeSession(GoogleLongAudioRequest request, GoogleLongAudioCostReview review, GoogleLongAudioJob job,
        GoogleLongAudioProtectedResponseObserver observer, GoogleLongAudioAdapter adapter, GoogleLongAudioOutputBoundary output,
        Func<CancellationToken, ValueTask> requireInstalled)
    {
        this.request = request; Review = review; this.job = job; this.observer = observer; this.adapter = adapter;
        this.output = output; this.requireInstalled = requireInstalled;
    }

    public GoogleLongAudioCostReview Review { get; }

    public Task<GoogleLongAudioJobProgress> AdvanceAsync(CancellationToken cancellationToken = default) => RunAsync(pollOnly: false, cancellationToken);
    public Task<GoogleLongAudioJobProgress> PollAsync(CancellationToken cancellationToken = default) => RunAsync(pollOnly: true, cancellationToken);

    private async Task<GoogleLongAudioJobProgress> RunAsync(bool pollOnly, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await running.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("This Long Audio job already has an action in progress.");
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await requireInstalled(token).ConfigureAwait(false);
            observer.ArmForJob(request, explicitlyConfirmed: true);
            return pollOnly ? await job.PollAsync(request, token).ConfigureAwait(false)
                : await job.AdvanceAsync(request, token).ConfigureAwait(false);
        }
        finally { running.Release(); }
    }

    public void Dispose()
    {
        // Closing a UI while a response is pending must not dispose its journal/capture underneath it.
        if (!running.Wait(0)) throw new InvalidOperationException("Wait for the current outcome before closing this Long Audio session.");
        try
        {
            if (disposed) return;
            disposed = true; observer.Disarm(); adapter.Dispose(); output.Dispose();
        }
        finally { running.Release(); }
    }
}

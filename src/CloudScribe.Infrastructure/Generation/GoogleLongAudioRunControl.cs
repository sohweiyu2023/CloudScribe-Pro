namespace CloudScribe.Infrastructure.Generation;

/// <summary>Stop between actions. It never cancels a paid POST or discards its uncertain outcome.</summary>
public sealed class GoogleLongAudioRunControl
{
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsStopRequested => stopped.Task.IsCompleted;
    public void RequestStop() => stopped.TrySetResult();
    public async Task WaitBeforePollAsync(TimeProvider clock, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delay = Task.Delay(TimeSpan.FromSeconds(5), clock, wait.Token); // Poll pacing, not a job expiry or attempt count.
        if (await Task.WhenAny(delay, stopped.Task).ConfigureAwait(false) == stopped.Task) await wait.CancelAsync().ConfigureAwait(false);
        else await delay.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}

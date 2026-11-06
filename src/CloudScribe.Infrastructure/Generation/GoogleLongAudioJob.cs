namespace CloudScribe.Infrastructure.Generation;

/// <summary>Durable operation-to-private-file orchestration; admission remains independently enforced.
/// A resumed unknown POST is never resubmitted. Polling does not create accepted media or settle spending.</summary>
public sealed class GoogleLongAudioJob(GoogleLongAudioAdapter adapter, GoogleLongAudioOutputBoundary output)
{
    public async Task<GoogleLongAudioJobProgress> AdvanceAsync(GoogleLongAudioRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkpoint = await adapter.SubmitOnceAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadCompletedOutputAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Caller prepares a fresh exact-response observer before this GET-only step.
    /// A POST observation is deliberately not reused as capture permission for polling.</summary>
    public async Task<GoogleLongAudioJobProgress> PollAsync(GoogleLongAudioRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkpoint = await adapter.PollOnceAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadCompletedOutputAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleLongAudioJobProgress> ReadCompletedOutputAsync(GoogleLongAudioRequest request,
        GoogleLongAudioCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var artifact = checkpoint.State == GoogleLongAudioState.CompletedUnverified
            ? await output.DownloadAndVerifyAsync(request, checkpoint, cancellationToken).ConfigureAwait(false) : null;
        return new(checkpoint, artifact);
    }
}

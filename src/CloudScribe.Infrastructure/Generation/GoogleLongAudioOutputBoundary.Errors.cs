using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioOutputBoundary
{
    private sealed record StorageResponse(byte[] Bytes, GoogleLongAudioObservation Observation, Exception? Failure);

    private static async Task<StorageResponse> ObserveStorageResponseAsync(IGoogleLongAudioResponseObserver observer,
        Guid ticket, GoogleLongAudioRequest request, HttpResponseMessage response, string bearer, CancellationToken token)
    {
        using var captured = new MemoryStream(); var state = GoogleResponseReadState.Complete; Exception? failure = null;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] bytes = new byte[4096];
            while (captured.Length <= 16_384)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, 16_385 - captured.Length)), token).ConfigureAwait(false);
                if (read == 0) break;
                captured.Write(bytes, 0, read);
            }
            if (captured.Length > 16_384) state = GoogleResponseReadState.Oversize;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { failure = error; state = error is OperationCanceledException ? GoogleResponseReadState.Cancelled : GoogleResponseReadState.Failed; }
        try
        {
            byte[] bytes = captured.ToArray();
            return new(bytes, await observer.ObserveStorageMetadataAsync(ticket, request, (int)response.StatusCode, bytes, state, bearer).ConfigureAwait(false), failure);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { return new(captured.ToArray(), new(null, state, GoogleLongAudioParseState.Unavailable, GoogleErrorStatus.Unknown, (int)captured.Length,
            GoogleExplanationRetentionState.StorageFailed, false, captured.Length > 16_384, true), failure is null ? error : new AggregateException(failure, error)); }
    }
}

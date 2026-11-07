using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioAdapter
{
    private sealed record BodyRead(byte[] Bytes, GoogleResponseReadState State, Exception? Failure);

    private static async Task<BodyRead> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        byte[] bytes = new byte[16_385]; int length = 0;
        try
        {
            using Stream stream = await content.ReadAsStreamAsync(token).WaitAsync(token).ConfigureAwait(false);
            while (length < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(length), token).AsTask().WaitAsync(token).ConfigureAwait(false);
                if (read == 0) return new(bytes[..length], GoogleResponseReadState.Complete, null);
                length += read;
            }
            return new(bytes, GoogleResponseReadState.Oversize, new InvalidDataException("Long Audio operation metadata exceeded its local bound."));
        }
        catch (Exception error) when (NonFatal(error))
        { return new(bytes[..length], error is OperationCanceledException ? GoogleResponseReadState.Cancelled : GoogleResponseReadState.Failed, error); }
    }

    private static GoogleLongAudioCheckpoint ResolveOutcome(GoogleLongAudioCheckpoint current, BodyRead body,
        bool post, int status, out Exception? parseFailure)
    {
        parseFailure = null;
        if (status is < 200 or >= 300)
            return current with { State = post && status is >= 400 and < 500 and not 408 and not 429 ? GoogleLongAudioState.Failed : current.State };
        if (body.State != GoogleResponseReadState.Complete) return current;
        try { return ParseOperation(current, body.Bytes); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        { parseFailure = error; return current; }
    }

    private async Task<GoogleLongAudioObservation> ObserveAsync(Guid ticket, GoogleLongAudioRequest request, int status,
        BodyRead body, string accessToken, bool operationError)
    {
        try
        {
            var result = await _observer.ObserveAsync(ticket, request, status, body.Bytes, body.State, accessToken, operationError).ConfigureAwait(false);
            if (result.ObservedBytes == body.Bytes.Length && result.ReadState == body.State && Enum.IsDefined(result.ParseState)
                && Enum.IsDefined(result.ErrorStatus) && Enum.IsDefined(result.Retention)) return result;
        }
        catch (Exception error) when (NonFatal(error)) { }
        return new(null, body.State, GoogleLongAudioParseState.Unavailable, GoogleErrorStatus.NotProvided,
            body.Bytes.Length, GoogleExplanationRetentionState.StorageFailed, false, true, true);
    }

    private async Task<GoogleLongAudioCheckpoint> CompleteObservedResponseAsync(GoogleLongAudioCheckpoint current,
        GoogleLongAudioRequest request, Guid ticket, bool post, int status, BodyRead body, string accessToken)
    {
        var outcome = ResolveOutcome(current, body, post, status, out var parseFailure) with { PollWaitDeadlineElapsed = false };
        outcome = outcome with { FailureKind = GoogleLongAudioFailureClassifier.Classify(status, outcome.RpcCode, body.Bytes, body.State) };
        bool operationError = ContainsOperationError(status, body);
        GoogleLongAudioCheckpoint saved;
        try
        {
            saved = _journal.Save(current, outcome with
            { HttpStatus = status, Revision = current.Revision + 1, OperationParseFailed = parseFailure is not null });
        }
        catch (Exception journalError) when (NonFatal(journalError))
        {
            var retained = await ObserveAsync(ticket, request, status, body, accessToken, operationError).ConfigureAwait(false);
            Exception original = body.Failure ?? parseFailure ?? journalError;
            GoogleLongAudioJournalFailure.Attach(original, status, retained);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
        // Durable provider outcome precedes observer storage; failed journal persistence still attempted capture above.
        var observation = await ObserveAsync(ticket, request, status, body, accessToken, operationError).ConfigureAwait(false);
        try { saved = _journal.Save(saved, saved with { Observation = observation, Revision = saved.Revision + 1 }); }
        catch (Exception journalError) when (NonFatal(journalError))
        { GoogleLongAudioJournalFailure.Attach(journalError, status, observation); throw; }
        _journal.FinishObservation(saved);
        if (body.Failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(body.Failure).Throw();
        if (parseFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(parseFailure).Throw();
        return saved;
    }

    private static bool ContainsOperationError(int status, BodyRead body)
    {
        if (status != 200) return false;
        try
        {
            using var json = JsonDocument.Parse(body.Bytes, new JsonDocumentOptions { MaxDepth = 8 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException) { return false; }
    }

    private void EndObservation(Guid ticket, Guid attempt)
    {
        try { _observer.FinishResponse(ticket); }
        catch (Exception error) when (NonFatal(error))
        {
            // Never replace an already-durable provider outcome with an observer cleanup exception.
            try { _journal.MarkObservationPending(attempt); }
            catch (Exception storageError) when (NonFatal(storageError)) { }
        }
    }

    private static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

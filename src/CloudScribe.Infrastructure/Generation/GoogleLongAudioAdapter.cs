using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Inactive Long Audio transport boundary. One durable POST claim, then explicit bounded GET-only resume.
/// Completion is never accepted media. Production admission independently binds the installed scope and cumulative budget.
/// </summary>
public sealed partial class GoogleLongAudioAdapter : IDisposable
{
    private readonly HttpClient _http;
    private readonly ITransientCredentialResolver _credentials;
    private readonly IGoogleLongAudioAdmission _admission;
    private readonly GoogleLongAudioFileJournal _journal;
    private readonly IGoogleLongAudioResponseObserver _observer;
    private readonly ILogger? logger;

    /// <param name="credentials">Existing transient resolver; no new credential storage or API is supplied.</param>
    /// <param name="admission">Trusted independent exact-request authority and readiness boundary.</param>
    /// <param name="journal">Private durable POST-claim journal.</param>
    /// <param name="observer">Mandatory exact-request protected response capture and readback boundary.</param>
    /// <param name="handler">Trusted transport, without retry/redirect/auth-forwarding handlers. Null uses a redirect-free handler.</param>
    /// <param name="logger">Optional privacy-safe transport observations; never raw provider text or credentials.</param>
    public GoogleLongAudioAdapter(ITransientCredentialResolver credentials, IGoogleLongAudioAdmission admission,
        GoogleLongAudioFileJournal journal, IGoogleLongAudioResponseObserver observer, HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.logger = logger;
        _http = new HttpClient(handler ?? GoogleLongAudioTransport.CreateHandler())
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Deadline for one idempotent status GET. When it passes, the saved operation stays pollable and the
    /// next status check runs; nothing is resubmitted. POSTs never receive this deadline.</summary>
    public TimeSpan PollDeadline { get; init; } = GoogleLongAudioTransport.PollResponseDeadline;

    public async Task<GoogleLongAudioCheckpoint> SubmitOnceAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_journal.ReadIfExists(request.AttemptId) is { } existing)
        { RequireBinding(existing, request); return existing; }
        if (!_observer.IsReadyFor(request)) throw new InvalidOperationException("Exact protected Long Audio response capture is not ready.");
        using var responseLease = _journal.AcquireResponseLease();
        // A failed funding review must not manufacture an uncertain POST journal. Admission below still
        // revalidates and durably commits the exact request before ExchangeAsync can touch the network.
        if (_admission is IGoogleLongAudioSubmissionPreflight preflight)
            await preflight.RequireBeforeJournalAsync(request, cancellationToken).ConfigureAwait(false);
        bool claimed = _journal.TryClaim(request);
        var initial = _journal.Load(request.AttemptId);
        RequireBinding(initial, request);
        if (!claimed) return initial; // Includes interrupted/unknown POST: never resend.
        Guid ticket = _observer.Begin(request);
        try
        {
            await _admission.RequireSubmissionAuthorizedAsync(request, cancellationToken).ConfigureAwait(false);
            // An arbitrary local deadline can lose the operation name after Google has accepted a POST.
            // Keep caller cancellation and the durable unknown-outcome/no-resend rules instead.
            return await ExchangeAsync(initial, request, ticket, true, cancellationToken, cancellationToken).ConfigureAwait(false);
        }
        finally { EndObservation(ticket, request.AttemptId); }
    }

    public async Task<GoogleLongAudioCheckpoint> PollOnceAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var current = _journal.Load(request.AttemptId);
        RequireBinding(current, request);
        if (current.State != GoogleLongAudioState.Pollable) return current;
        if (!_observer.IsReadyFor(request)) throw new InvalidOperationException("Exact protected Long Audio poll capture is not ready.");
        using var responseLease = _journal.AcquireResponseLease();
        _journal.MarkObservationPending(request.AttemptId);
        Guid ticket = _observer.Begin(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (PollDeadline > TimeSpan.Zero && PollDeadline != Timeout.InfiniteTimeSpan) deadline.CancelAfter(PollDeadline);
        try
        {
            try
            {
                return await ExchangeAsync(current, request, ticket, false, deadline.Token, cancellationToken).ConfigureAwait(false);
            }
            finally { EndObservation(ticket, request.AttemptId); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // A status GET is idempotent: an unanswered one is replaced by the next check. The journal stays pollable.
            var after = _journal.Load(request.AttemptId);
            RequireBinding(after, request);
            if (after.State == GoogleLongAudioState.Pollable && after.HttpStatus == 200 && !after.OperationParseFailed
                && after.Observation is
                {
                    ReadbackVerified: true, Truncated: true,
                    ReadState: CloudScribe.Domain.Generation.GoogleResponseReadState.Cancelled,
                    Retention: CloudScribe.Domain.Generation.GoogleExplanationRetentionState.SavedPrivately
                })
            {
                after = _journal.Save(after, after with { PollWaitDeadlineElapsed = true, Revision = after.Revision + 1 });
                _journal.FinishObservation(after);
            }
            if (logger is not null) CloudScribe.Application.Logging.CloudScribeLog.LongAudioPollDeadline(logger, request.AttemptId, (long)PollDeadline.TotalSeconds, after.HasVerifiedPollDeadline);
            return after;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One owned exchange preserves the original exception, private response, journal and secondary diagnostic failures without a retry or exception replacement.")]
    private async Task<GoogleLongAudioCheckpoint> ExchangeAsync(GoogleLongAudioCheckpoint current, GoogleLongAudioRequest request,
        Guid ticket, bool post, CancellationToken token, CancellationToken callerToken)
    {
        byte[]? payload = post ? request.CopyPayload() : null;
        string path = payload is null ? current.OperationName! : $"projects/{current.ProjectNumber}/locations/{current.Location}:synthesizeLongAudio";
        string accessToken = string.Empty, correlation = Guid.NewGuid().ToString("N");
        long started = System.Diagnostics.Stopwatch.GetTimestamp(); int status = 0; bool sendStarted = false;
        HttpResponseMessage? response = null; BodyRead? body = null; Exception? failure = null, diagnosticsFailure = null;
        GoogleLongAudioCheckpoint? saved = null;
        try
        {
            accessToken = await _credentials.ResolveAccessTokenAsync(current.CredentialReferenceId, token).AsTask().WaitAsync(token).ConfigureAwait(false);
            GoogleLongAudioValidation.RequireAccessToken(accessToken);
            if (!_observer.IsActiveFor(ticket, request)) throw new InvalidOperationException("Exact protected response capture expired or changed before dispatch.");
            using var message = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post,
                new Uri("https://texttospeech.googleapis.com/v1/" + path));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (payload is not null) { message.Content = new ByteArrayContent(payload); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
            sendStarted = true;
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            status = (int)response.StatusCode; body = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
            saved = await CompleteObservedResponseAsync(current, request, ticket, post, status, body, accessToken).ConfigureAwait(false);
        }
        catch (Exception error) when (NonFatal(error))
        {
            failure = error;
            if (status == 0)
            {
                var missing = new BodyRead([], error is OperationCanceledException ? CloudScribe.Domain.Generation.GoogleResponseReadState.Cancelled : CloudScribe.Domain.Generation.GoogleResponseReadState.Failed, error);
                var observation = await ObserveAsync(ticket, request, 0, missing, accessToken, false).ConfigureAwait(false);
                try { saved = _journal.Save(current, current with { Observation = observation, PollWaitDeadlineElapsed = false, Revision = current.Revision + 1 }); }
                catch (Exception journalError) when (NonFatal(journalError)) { GoogleLongAudioJournalFailure.Attach(error, 0, observation); }
            }
        }
        try
        {
            var observed = saved?.Observation ?? (failure is null ? null : GoogleLongAudioJournalFailure.Inspect(failure)?.Observation);
            if (observed is null)
            {
                try { observed = _journal.Load(request.AttemptId).Observation; }
                catch (Exception readError) when (NonFatal(readError)) { diagnosticsFailure = readError; }
            }
            GoogleLongAudioTransportLog.Write(logger, request, correlation,
                post ? CloudScribe.Application.Generation.LongAudioEndpoint.SynthesizeV1 : CloudScribe.Application.Generation.LongAudioEndpoint.OperationV1,
                status, body?.State ?? observed?.ReadState, body?.Bytes.Length ?? observed?.ObservedBytes ?? 0,
                checked((long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds), observed?.PrivateRecordId,
                failure?.HResult ?? diagnosticsFailure?.HResult, response?.Headers, sendStarted, callerToken.IsCancellationRequested);
        }
        catch (Exception loggingError) when (NonFatal(loggingError))
        {
            diagnosticsFailure = diagnosticsFailure is null ? loggingError : new AggregateException(diagnosticsFailure, loggingError);
            try { _journal.MarkObservationPending(request.AttemptId); }
            catch (Exception journalError) when (NonFatal(journalError)) { diagnosticsFailure = new AggregateException(diagnosticsFailure, journalError); }
        }
        finally
        {
            try { response?.Dispose(); }
            catch (Exception cleanupError) when (NonFatal(cleanupError)) { diagnosticsFailure = diagnosticsFailure is null ? cleanupError : new AggregateException(diagnosticsFailure, cleanupError); }
        }
        if (failure is not null && diagnosticsFailure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (failure is not null) throw new AggregateException("Original Long Audio failure and local evidence failure; saved outcomes remain authoritative.", failure, diagnosticsFailure!);
        if (diagnosticsFailure is not null) throw new InvalidOperationException("Long Audio outcome was recorded but local evidence failed; reconcile before continuing.", diagnosticsFailure);
        return saved ?? throw new InvalidOperationException("No durable Long Audio outcome is available; never resubmit this claimed request.");
    }
    private static GoogleLongAudioCheckpoint ParseOperation(GoogleLongAudioCheckpoint current, byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Long Audio operation response.");
        GoogleLongAudioValidation.RequireUnique(root);
        string? name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (!GoogleLongAudioValidation.IsOperation(name, current.ProjectNumber, current.Location)
            || current.OperationName is not null && !string.Equals(name, current.OperationName, StringComparison.Ordinal))
            throw new InvalidDataException("Long Audio operation is outside its exact submitted scope.");
        bool done = false;
        if (root.TryGetProperty("done", out var d))
        {
            if (d.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid operation completion flag.");
            done = d.GetBoolean();
        }
        int? code = null;
        if (root.TryGetProperty("error", out var error))
        {
            if (!done || error.ValueKind != JsonValueKind.Object || root.TryGetProperty("response", out _)
                || !error.TryGetProperty("code", out var c) || !c.TryGetInt32(out int value) || value is < 1 or > 16)
                throw new InvalidDataException("Invalid Long Audio terminal metadata.");
            code = value; // Never retain message/details, arbitrary metadata or response bodies.
        }
        else if (!done && root.TryGetProperty("response", out _)) throw new InvalidDataException("Premature operation response.");
        return current with
        {
            OperationName = name,
            RpcCode = code,
            State = !done ? GoogleLongAudioState.Pollable : code.HasValue ? GoogleLongAudioState.Failed : GoogleLongAudioState.CompletedUnverified
        };
    }

    private static void RequireBinding(GoogleLongAudioCheckpoint checkpoint, GoogleLongAudioRequest request)
    {
        if (!string.Equals(checkpoint.BindingSha256, request.BindingSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Long Audio attempt identity cannot be rebound.");
    }

    public void Dispose() => _http.Dispose();
}

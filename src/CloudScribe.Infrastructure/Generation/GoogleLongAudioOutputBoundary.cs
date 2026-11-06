using System.Net.Http.Headers;
using CloudScribe.Application.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>GET-only private output verification, bound to the installed scope, retained claim and durable completed operation.</summary>
public sealed partial class GoogleLongAudioOutputBoundary : IDisposable
{
    private readonly ITransientCredentialResolver credentials;
    private readonly IGoogleLongAudioOutputAuthority authority;
    private readonly GoogleLongAudioFileJournal journal;
    private readonly INativeMediaTool native;
    private readonly string ffmpeg, ffprobe, root;
    private readonly GoogleLongAudioMediaTools tools;
    private readonly TimeProvider clock;
    private readonly HttpClient http;
    private readonly Func<GoogleLongAudioRequest, IGoogleLongAudioResponseObserver> observers;
    private readonly ILogger? logger;

    public GoogleLongAudioOutputBoundary(ITransientCredentialResolver credentials, IGoogleLongAudioOutputAuthority authority,
        GoogleLongAudioFileJournal journal, INativeMediaTool native, GoogleLongAudioMediaTools tools, string privateOutputRoot,
        TimeProvider clock, Func<GoogleLongAudioRequest, IGoogleLongAudioResponseObserver> observers, HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.observers = observers ?? throw new ArgumentNullException(nameof(observers));
        this.logger = logger;
        this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        if (!Path.IsPathFullyQualified(tools.FfmpegPath) || !Path.IsPathFullyQualified(tools.FfprobePath))
            throw new ArgumentException("Native verifier paths must be absolute.", nameof(tools));
        ffmpeg = Path.GetFullPath(tools.FfmpegPath); ffprobe = Path.GetFullPath(tools.FfprobePath);
        if (!Path.IsPathFullyQualified(privateOutputRoot)) throw new ArgumentException("Private output root must be absolute.", nameof(privateOutputRoot));
        root = Path.GetFullPath(privateOutputRoot); RequirePhysical(root);
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(12) };
    }

    public async Task<GoogleLongAudioVerifiedArtifact> DownloadAndVerifyAsync(GoogleLongAudioRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await DownloadAndVerifyAsync(request, journal.Load(request.AttemptId), token).ConfigureAwait(false);
    }

    public async Task<GoogleLongAudioVerifiedArtifact> DownloadAndVerifyAsync(GoogleLongAudioRequest request,
        GoogleLongAudioCheckpoint expectedCheckpoint, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkpoint = journal.Load(request.AttemptId);
        if (checkpoint != expectedCheckpoint)
            throw new InvalidOperationException("Output journal differs from the exact completed operation supplied by the caller.");
        RequireCompletedBinding(request, checkpoint);
        var allowed = await authority.RequireReadAuthorizedAsync(request, checkpoint, token).ConfigureAwait(false);
        RequireAllowance(request, allowed);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (allowed.ReadNotAfterUtc is { } deadline) bound.CancelAfter(deadline - clock.GetUtcNow());
        RequirePhysical(root); Directory.CreateDirectory(root); RequirePhysical(root);
        string prefix = Path.Combine(root, request.AttemptId.ToString("N"));
        RequirePhysical(prefix + ".download.lease");
        using var lease = new FileStream(prefix + ".download.lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identity = await LoadOrPinIdentityAsync(request, allowed, prefix, bound.Token).ConfigureAwait(false);
        string destination = prefix + (request.OutputGcsUri.EndsWith(".mp3", StringComparison.Ordinal) ? ".mp3" : ".wav");
        if (!File.Exists(destination)) await DownloadNewAsync(request, identity, destination, bound.Token).ConfigureAwait(false);
        RequirePhysical(destination);
        using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        string sha = await VerifyBytesAsync(destination, identity, bound.Token).ConfigureAwait(false);
        await VerifyNativeAsync(destination, allowed, bound.Token).ConfigureAwait(false);
        if (!string.Equals(sha, await VerifyBytesAsync(destination, identity, bound.Token).ConfigureAwait(false), StringComparison.Ordinal))
            throw new InvalidDataException("Local output changed during native verification.");
        if (journal.Load(request.AttemptId) != expectedCheckpoint)
            throw new InvalidOperationException("Completed operation changed during output verification; preserved media requires reconciliation.");
        return new(identity, destination, sha, identity.Bytes);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One owned GET preserves the original failure, response capture and cleanup failure without synthesis or retries.")]
    private async Task<HttpResponseMessage> GetAsync(GoogleLongAudioRequest request, Uri uri, CancellationToken token)
    {
        var observer = observers(request);
        if (!observer.IsReadyFor(request)) throw new InvalidOperationException("Exact protected storage-error capture is not ready.");
        Guid ticket = observer.Begin(request);
        string correlation = Guid.NewGuid().ToString("N"); long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string bearer = string.Empty; bool sendStarted = false, received = false;
        try
        {
            bearer = await credentials.ResolveAccessTokenAsync(request.CredentialReferenceId, token).AsTask().WaitAsync(token).ConfigureAwait(false);
            if (bearer.Length is < 1 or > 16_384 || !bearer.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='))
                throw new InvalidOperationException("Storage-read credential is unavailable or malformed.");
            if (!observer.IsActiveFor(ticket, request)) throw new InvalidOperationException("Protected storage capture changed before the GET.");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            sendStarted = true;
            var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            received = true;
            bool metadata = !uri.Query.Contains("alt=media", StringComparison.Ordinal);
            try
            {
                StorageResponse? captured = response.StatusCode != System.Net.HttpStatusCode.OK || metadata
                    ? await ObserveStorageResponseAsync(observer, ticket, request, response, bearer, token).ConfigureAwait(false) : null;
                GoogleLongAudioTransportLog.Write(logger, request, correlation, metadata ? LongAudioEndpoint.StorageMetadataV1 : LongAudioEndpoint.StorageMediaV1,
                    (int)response.StatusCode, captured?.Observation.ReadState, captured?.Bytes.Length ?? 0,
                    checked((long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds), captured?.Observation.PrivateRecordId,
                    null, response.Headers, true, token.IsCancellationRequested);
                if (response.StatusCode != System.Net.HttpStatusCode.OK || captured is { Observation.ReadbackVerified: false }
                    || captured is { Observation.ReadState: not CloudScribe.Domain.Generation.GoogleResponseReadState.Complete })
                    throw new GoogleLongAudioStorageReadException((int)response.StatusCode, captured!.Observation, captured.Failure);
                if (captured is not null)
                {
                    // Capture the received metadata before parsing, including malformed HTTP-200 bodies.
                    // This is response evidence, not an assertion that Google returned an error.
                    var original = response.Content; response.Content = new ByteArrayContent(captured.Bytes); original.Dispose();
                }
                return response;
            }
            catch (Exception original) when (NonFatalStorage(original))
            {
                try { response.Dispose(); }
                catch (Exception cleanup) when (NonFatalStorage(cleanup)) { throw new AggregateException("Storage read and response cleanup failed; never resubmit synthesis.", original, cleanup); }
                throw;
            }
        }
        catch (Exception original) when (!received && NonFatalStorage(original))
        {
            try
            {
                var state = original is OperationCanceledException ? CloudScribe.Domain.Generation.GoogleResponseReadState.Cancelled : CloudScribe.Domain.Generation.GoogleResponseReadState.Failed;
                var observation = await observer.ObserveStorageMetadataAsync(ticket, request, 0, ReadOnlyMemory<byte>.Empty, state, bearer).ConfigureAwait(false);
                GoogleLongAudioTransportLog.Write(logger, request, correlation,
                    uri.Query.Contains("alt=media", StringComparison.Ordinal) ? LongAudioEndpoint.StorageMediaV1 : LongAudioEndpoint.StorageMetadataV1,
                    0, state, 0, checked((long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds), observation.PrivateRecordId,
                    original.HResult, null, sendStarted, token.IsCancellationRequested);
            }
            catch (Exception evidence) when (NonFatalStorage(evidence)) { throw new AggregateException("Storage access and its private evidence failed; original outcome preserved.", original, evidence); }
            throw;
        }
        finally
        {
            try { observer.FinishResponse(ticket); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { /* Cleanup cannot replace the actual storage response. This boundary never dispatches synthesis. */ }
        }
    }

    private static bool NonFatalStorage(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    public void Dispose() => http.Dispose();
}

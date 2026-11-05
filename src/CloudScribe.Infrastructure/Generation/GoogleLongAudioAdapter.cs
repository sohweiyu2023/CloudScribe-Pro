using System.Net.Http.Headers;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Inactive Long Audio transport boundary. One durable POST claim, then explicit bounded GET-only resume.
/// Completion is never accepted media. No production admission implementation or DI route is supplied.
/// </summary>
public sealed partial class GoogleLongAudioAdapter : IDisposable
{
    private readonly HttpClient _http;
    private readonly ITransientCredentialResolver _credentials;
    private readonly IGoogleLongAudioAdmission _admission;
    private readonly GoogleLongAudioFileJournal _journal;
    private readonly IGoogleLongAudioResponseObserver _observer;

    /// <param name="credentials">Existing transient resolver; no new credential storage or API is supplied.</param>
    /// <param name="admission">Trusted independent exact-request authority and readiness boundary.</param>
    /// <param name="journal">Private durable POST-claim journal.</param>
    /// <param name="observer">Mandatory exact-request protected response capture and readback boundary.</param>
    /// <param name="handler">Trusted transport, without retry/redirect/auth-forwarding handlers. Null uses a redirect-free handler.</param>
    public GoogleLongAudioAdapter(ITransientCredentialResolver credentials, IGoogleLongAudioAdmission admission,
        GoogleLongAudioFileJournal journal, IGoogleLongAudioResponseObserver observer, HttpMessageHandler? handler = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(12) };
    }

    public async Task<GoogleLongAudioCheckpoint> SubmitOnceAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_journal.ReadIfExists(request.AttemptId) is { } existing)
        { RequireBinding(existing, request); return existing; }
        if (!_observer.IsReadyFor(request)) throw new InvalidOperationException("Exact protected Long Audio response capture is not ready.");
        using var responseLease = _journal.AcquireResponseLease();
        bool claimed = _journal.TryClaim(request);
        var initial = _journal.Load(request.AttemptId);
        RequireBinding(initial, request);
        if (!claimed) return initial; // Includes interrupted/unknown POST: never resend.
        Guid ticket = _observer.Begin(request);
        try
        {
            await _admission.RequireSubmissionAuthorizedAsync(request, cancellationToken).ConfigureAwait(false);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(25));
            return await ExchangeAsync(initial, request, ticket, true, bound.Token).ConfigureAwait(false);
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
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(25));
            return await ExchangeAsync(current, request, ticket, false, bound.Token).ConfigureAwait(false);
        }
        finally { EndObservation(ticket, request.AttemptId); }
    }

    private async Task<GoogleLongAudioCheckpoint> ExchangeAsync(GoogleLongAudioCheckpoint current, GoogleLongAudioRequest request,
        Guid ticket, bool post, CancellationToken token)
    {
        byte[]? payload = post ? request.CopyPayload() : null;
        string path = payload is null ? current.OperationName!
            : $"projects/{current.ProjectNumber}/locations/{current.Location}:synthesizeLongAudio";
        string accessToken = await _credentials.ResolveAccessTokenAsync(current.CredentialReferenceId, token).AsTask().WaitAsync(token).ConfigureAwait(false);
        GoogleLongAudioValidation.RequireAccessToken(accessToken);
        if (!_observer.IsActiveFor(ticket, request)) throw new InvalidOperationException("Exact protected response capture expired or changed before dispatch.");
        using var message = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri("https://texttospeech.googleapis.com/v1/" + path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (payload is not null)
        {
            message.Content = new ByteArrayContent(payload);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        var body = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
        return await CompleteObservedResponseAsync(current, request, ticket, post, status, body, accessToken).ConfigureAwait(false);
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

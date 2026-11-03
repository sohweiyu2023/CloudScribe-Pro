using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleGenerationHttpTransport
{
    private readonly HttpClient _httpClient;
    private readonly ITransientCredentialResolver _credentialResolver;
    private readonly Uri _allowedOrigin;
    private readonly ILogger<GoogleGenerationHttpTransport> _logger;
    private readonly GooglePrivateErrorDiagnostics? _privateDiagnostics;
    private readonly CompletionWorkflowGate? _completionGate;

    public GoogleGenerationHttpTransport(HttpClient httpClient, ITransientCredentialResolver credentialResolver, Uri allowedOrigin, ILogger<GoogleGenerationHttpTransport>? logger = null, GooglePrivateErrorDiagnostics? privateDiagnostics = null,
        CompletionWorkflowGate? completionGate = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _allowedOrigin = ValidateOrigin(allowedOrigin);
        _logger = logger ?? NullLogger<GoogleGenerationHttpTransport>.Instance;
        _privateDiagnostics = privateDiagnostics;
        _completionGate = completionGate;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Single transport lifetime preserves original outcomes while independently observing absent and partial responses.")]
    public async Task<GoogleHttpTransportResponse> SendAsync(GoogleHttpTransportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEndpoint(request.Endpoint);
        if (string.IsNullOrWhiteSpace(request.CredentialReferenceId))
            throw new ArgumentException("Credential reference ID is required.", nameof(request));
        ValidateBoundedRequest(request.Payload, request.MaximumResponseBytes);
        byte[] wirePayload = request.Payload.ToArray();

        Guid correlationId = Guid.NewGuid();
        string correlation = correlationId.ToString("N");
        string payloadHash = CloudScribeLog.ObserveIsEnabled(_logger, LogLevel.Information) ? Convert.ToHexStringLower(SHA256.HashData(wirePayload)) : "logging-disabled";
        int status = 0;
        long started = Stopwatch.GetTimestamp();
        var lastStage = GoogleSynthesisStage.ResolvingCredential;
        void Log(GoogleSynthesisStage stage, GoogleErrorStatus category = GoogleErrorStatus.NotProvided)
        {
            lastStage = stage;
            CloudScribeLog.Observe(() => CloudScribeLog.GoogleSynthesisTransportStage(_logger, correlation, payloadHash, stage, status, category));
        }
        Log(GoogleSynthesisStage.ResolvingCredential);
        try
        {
            RequireCompletionSynthesis(request, wirePayload, correlationId);
            var token = await _credentialResolver.ResolveAccessTokenAsync(request.CredentialReferenceId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Credential resolver returned an empty access token.");

            using var message = CreateMessage(request.Endpoint, token, wirePayload);

            Log(GoogleSynthesisStage.Dispatching);
            ObserveWireContent(correlation, payloadHash, wirePayload.Length, request.Endpoint);
            if (request.RequirePrivateDiagnosticCapture && _privateDiagnostics?.IsActiveFor(request.PrivateDiagnosticTicket, wirePayload, requireProtectedEnvelope: true) != true)
                throw new PrivateDiagnosticNotReadyException();
            RequireCompletionSynthesis(request, wirePayload, correlationId);
            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            Log(GoogleSynthesisStage.ResponseHeaders);
            ObserveRequestId(correlation, response.Headers);
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
            byte[] bytes = await ReadObservedResponseAsync(response, request.MaximumResponseBytes, correlation,
                request.PrivateDiagnosticTicket, wirePayload, token, cancellationToken).ConfigureAwait(false);
            var category = response.StatusCode == System.Net.HttpStatusCode.OK ? GoogleErrorStatus.NotProvided : GoogleErrorStatusParser.Parse(bytes);
            Log(GoogleSynthesisStage.ResponseReadCompleted, category);
            ObserveProviderResponse(request.PrivateDiagnosticTicket, correlation, status, bytes, wirePayload, token);
            return new GoogleHttpTransportResponse(response.StatusCode, bytes, retryAfter, category,
                status == 400 ? GoogleErrorResponseDiagnosticParser.Parse(bytes).MessageHint : GoogleValidationMessageHint.NotExamined);
        }
        catch (OperationCanceledException)
        {
            ObserveMissingResponse(request, correlation, status, wirePayload);
            Log(GoogleSynthesisStage.Cancelled);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            ObserveMissingResponse(request, correlation, status, wirePayload);
            Log(GoogleSynthesisStage.Failed);
            throw;
        }
        finally { EndTransportObservation(request.PrivateDiagnosticTicket, correlation, lastStage, status, started); }
    }

    private static HttpRequestMessage CreateMessage(Uri endpoint, string token, byte[] payload)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new ByteArrayContent(payload);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return message;
    }

    internal Guid? BeginPrivateCapture(CloudScribe.Providers.Abstractions.GenerationProviderRequest request, string credentialReference, bool requireProtectedEnvelope = false)
    {
        try { return _privateDiagnostics?.Begin(request, credentialReference, requireProtectedEnvelope); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { return null; }
    }
    public bool IsPrivateCaptureReady(CloudScribe.Providers.Abstractions.GenerationProviderRequest request, string credentialReference) =>
        _privateDiagnostics?.IsReadyFor(request, credentialReference, requireProtectedEnvelope: true) == true;
    internal void EndPrivateCapture(Guid? ticket) => _privateDiagnostics?.End(ticket);

    private static void ValidateBoundedRequest(ReadOnlyMemory<byte> payload, int maximumResponseBytes)
    {
        if (payload.IsEmpty)
            throw new ArgumentException("Compiled request payload is required.", nameof(payload));
        if (maximumResponseBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
    }

    private void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !IsGoogleApisHost(endpoint.Host))
        {
            throw new InvalidOperationException("Google generation endpoint must be a credential-free absolute HTTPS Google APIs URI without a fragment.");
        }
        if (!string.Equals(endpoint.Scheme, _allowedOrigin.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(endpoint.Host, _allowedOrigin.Host, StringComparison.OrdinalIgnoreCase) || endpoint.Port != _allowedOrigin.Port)
            throw new InvalidOperationException("Google generation endpoint origin is not the pinned account origin.");
    }

    private static Uri ValidateOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            !IsGoogleApisHost(origin.Host))
        {
            throw new ArgumentException("Allowed origin must be a credential-free absolute HTTPS Google APIs URI without a fragment.", nameof(origin));
        }
        return origin;
    }

    private static bool IsGoogleApisHost(string host) =>
        string.Equals(host, "googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retry)
    {
        if (retry is null) return null;
        if (retry.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry.Date is { } date)
        {
            var value = date - DateTimeOffset.UtcNow;
            return value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }
        return null;
    }
}

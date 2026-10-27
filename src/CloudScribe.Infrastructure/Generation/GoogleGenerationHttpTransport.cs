using System.Net.Http.Headers;
using System.Security.Cryptography;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationHttpTransport
{
    private readonly HttpClient _httpClient;
    private readonly ITransientCredentialResolver _credentialResolver;
    private readonly Uri _allowedOrigin;
    private readonly ILogger<GoogleGenerationHttpTransport> _logger;

    public GoogleGenerationHttpTransport(HttpClient httpClient, ITransientCredentialResolver credentialResolver, Uri allowedOrigin, ILogger<GoogleGenerationHttpTransport>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _allowedOrigin = ValidateOrigin(allowedOrigin);
        _logger = logger ?? NullLogger<GoogleGenerationHttpTransport>.Instance;
    }

    public async Task<GoogleHttpTransportResponse> SendAsync(GoogleHttpTransportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEndpoint(request.Endpoint);
        if (string.IsNullOrWhiteSpace(request.CredentialReferenceId))
            throw new ArgumentException("Credential reference ID is required.", nameof(request));
        ValidateBoundedRequest(request.Payload, request.MaximumResponseBytes);

        string correlation = Guid.NewGuid().ToString("N");
        string payloadHash = _logger.IsEnabled(LogLevel.Information) ? Convert.ToHexStringLower(SHA256.HashData(request.Payload.Span)) : "logging-disabled";
        int status = 0;
        void Log(GoogleSynthesisStage stage, GoogleErrorStatus category = GoogleErrorStatus.NotProvided) =>
            CloudScribeLog.GoogleSynthesisTransportStage(_logger, correlation, payloadHash, stage, status, category);
        Log(GoogleSynthesisStage.ResolvingCredential);
        try
        {
            var token = await _credentialResolver.ResolveAccessTokenAsync(request.CredentialReferenceId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Credential resolver returned an empty access token.");

            using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new ByteArrayContent(request.Payload.ToArray());
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Log(GoogleSynthesisStage.Dispatching);
            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            Log(GoogleSynthesisStage.ResponseHeaders);
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
            byte[] bytes = await ReadResponseBytesAsync(response, request.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            var category = response.StatusCode == System.Net.HttpStatusCode.OK ? GoogleErrorStatus.NotProvided : GoogleErrorStatusParser.Parse(bytes);
            Log(GoogleSynthesisStage.ResponseReadCompleted, category);
            if (status != 200)
            {
                var details = GoogleErrorDetailParser.Parse(bytes);
                CloudScribeLog.GoogleSynthesisErrorDetails(_logger, correlation, status, details.Reason, details.Domain, details.Field);
            }
            return new GoogleHttpTransportResponse(response.StatusCode, bytes, retryAfter, category);
        }
        catch (OperationCanceledException)
        {
            Log(GoogleSynthesisStage.Cancelled);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Log(GoogleSynthesisStage.Failed);
            throw;
        }
    }

    private static async Task<byte[]> ReadResponseBytesAsync(HttpResponseMessage response, int maximumResponseBytes, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var rented = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumResponseBytes)
                throw new InvalidDataException("Google response exceeded the configured bounded response size.");
            buffer.Write(rented, 0, read);
        }

        return buffer.ToArray();
    }

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

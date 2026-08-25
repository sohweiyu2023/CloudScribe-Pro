using System.Net;
using System.Net.Http.Headers;

namespace CloudScribe.Infrastructure.Generation;

public interface ITransientCredentialResolver
{
    ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken);
}

public sealed record GoogleHttpTransportRequest(
    Uri Endpoint,
    string CredentialReferenceId,
    ReadOnlyMemory<byte> Payload,
    int MaximumResponseBytes = 16 * 1024 * 1024);

public sealed record GoogleHttpTransportResponse(HttpStatusCode StatusCode, ReadOnlyMemory<byte> Body, TimeSpan? RetryAfter);

public sealed class GoogleGenerationHttpTransport
{
    private readonly HttpClient _httpClient;
    private readonly ITransientCredentialResolver _credentialResolver;
    private readonly Uri _allowedOrigin;

    public GoogleGenerationHttpTransport(HttpClient httpClient, ITransientCredentialResolver credentialResolver, Uri allowedOrigin)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _allowedOrigin = ValidateOrigin(allowedOrigin);
    }

    public async Task<GoogleHttpTransportResponse> SendAsync(GoogleHttpTransportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEndpoint(request.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CredentialReferenceId);
        ValidateBoundedRequest(request.Payload, request.MaximumResponseBytes);

        var token = await _credentialResolver.ResolveAccessTokenAsync(request.CredentialReferenceId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Credential resolver returned an empty access token.");

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new ByteArrayContent(request.Payload.ToArray());
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var rented = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > request.MaximumResponseBytes)
                throw new InvalidDataException("Google response exceeded the configured bounded response size.");
            buffer.Write(rented, 0, read);
        }

        return new GoogleHttpTransportResponse(response.StatusCode, buffer.ToArray(), retryAfter);
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
        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google generation endpoint must be absolute HTTPS.");
        if (!string.Equals(endpoint.Scheme, _allowedOrigin.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(endpoint.Host, _allowedOrigin.Host, StringComparison.OrdinalIgnoreCase) || endpoint.Port != _allowedOrigin.Port)
            throw new InvalidOperationException("Google generation endpoint origin is not the pinned account origin.");
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("Credentials must never be embedded in the endpoint URI.");
    }

    private static Uri ValidateOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(origin.UserInfo))
            throw new ArgumentException("Allowed origin must be credential-free absolute HTTPS.", nameof(origin));
        return origin;
    }

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

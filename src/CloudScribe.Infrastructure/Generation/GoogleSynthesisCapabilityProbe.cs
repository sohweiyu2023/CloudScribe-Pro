using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Establishes provider-side evidence that the configured credential/project can reach the exact
/// admitted Cloud Text-to-Speech synthesis method without synthesizing audio or creating a
/// billable request. The probe intentionally posts an empty JSON object, which cannot constitute
/// a valid synthesis request. Only Google's authenticated INVALID_ARGUMENT response is admitted as
/// positive capability evidence; success, auth/service failures, redirects, or other errors fail
/// closed.
/// </summary>
public sealed class GoogleSynthesisCapabilityProbe(
    HttpClient httpClient,
    ITransientCredentialResolver credentialResolver,
    TimeProvider timeProvider)
{
    private const int MaximumEvidenceBodyBytes = 64 * 1024;

    public async Task<GoogleSynthesisCapabilityEvidence> VerifyAsync(
        ProviderAccountReference account,
        string projectId,
        Uri synthesisEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(synthesisEndpoint);
        ValidateEndpointBinding(account, synthesisEndpoint);

        CredentialReference credential = account.CredentialReference
            ?? throw new InvalidOperationException("Google synthesis capability verification requires an admitted credential reference.");
        string accessToken = await credentialResolver
            .ResolveAccessTokenAsync(credential.TargetName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Google synthesis capability verification did not receive a transient access token.");

        // Deliberately invalid and input-free: Google cannot synthesize audio from this payload.
        using HttpRequestMessage request = new(HttpMethod.Post, synthesisEndpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-goog-user-project", projectId.Trim());

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        byte[] body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException(
                $"Google synthesis capability probe was not admitted: expected an input-validation rejection but received HTTP {(int)response.StatusCode}.");
        }

        string? providerStatus = TryReadGoogleErrorStatus(body);
        if (!string.Equals(providerStatus, "INVALID_ARGUMENT", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Google synthesis capability probe returned HTTP 400 without provider INVALID_ARGUMENT evidence.");
        }

        DateTimeOffset capturedAtUtc = timeProvider.GetUtcNow();
        if (capturedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Google synthesis capability verification requires a UTC time provider.");
        string evidenceHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return new GoogleSynthesisCapabilityEvidence(
            capturedAtUtc,
            $"google-synthesis-probe:invalid-argument:sha256:{evidenceHash}");
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaximumEvidenceBodyBytes)
            throw new InvalidDataException("Google synthesis capability probe response exceeded the evidence size limit.");

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream destination = new();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumEvidenceBodyBytes)
                throw new InvalidDataException("Google synthesis capability probe response exceeded the evidence size limit.");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static string? TryReadGoogleErrorStatus(byte[] body)
    {
        if (body.Length == 0)
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
                return null;
            return error.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateEndpointBinding(ProviderAccountReference account, Uri synthesisEndpoint)
    {
        if (!synthesisEndpoint.IsAbsoluteUri
            || !string.Equals(synthesisEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !synthesisEndpoint.IsDefaultPort
            || !string.IsNullOrEmpty(synthesisEndpoint.UserInfo)
            || !string.IsNullOrEmpty(synthesisEndpoint.Query)
            || !string.IsNullOrEmpty(synthesisEndpoint.Fragment)
            || string.IsNullOrWhiteSpace(synthesisEndpoint.AbsolutePath)
            || string.Equals(synthesisEndpoint.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google synthesis capability probe requires an explicit credential-free HTTPS synthesis API path.");
        }

        Uri admittedOrigin = account.EndpointOrigin
            ?? throw new InvalidOperationException("Google synthesis capability verification requires an admitted endpoint origin.");
        Uri suppliedOrigin = new(synthesisEndpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        if (Uri.Compare(
                admittedOrigin,
                suppliedOrigin,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException("Google synthesis capability endpoint origin does not match the admitted provider account origin.");
        }
    }
}

public sealed record GoogleSynthesisCapabilityEvidence(
    DateTimeOffset CapturedAtUtc,
    string ProvenanceId);

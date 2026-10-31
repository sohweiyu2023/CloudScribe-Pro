using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

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
    TimeProvider timeProvider,
    ILogger<GoogleSynthesisCapabilityProbe>? logger = null, CompletionWorkflowGate? completionGate = null)
{
    private const int MaximumEvidenceBodyBytes = 64 * 1024;

    public Task<GoogleSynthesisCapabilityEvidence> VerifyAsync(ProviderAccountReference account, string projectId,
        Uri synthesisEndpoint, CancellationToken cancellationToken = default) =>
        VerifyObservedAsync(account, projectId, synthesisEndpoint, Guid.NewGuid(), cancellationToken);

    internal async Task<GoogleSynthesisCapabilityEvidence> VerifyObservedAsync(
        ProviderAccountReference account, string projectId, Uri synthesisEndpoint,
        Guid diagnosticRefreshId, CancellationToken cancellationToken)
    {
        Guid refreshId = completionGate?.CurrentRefreshId(diagnosticRefreshId) ?? diagnosticRefreshId;
        GoogleCatalogRefreshPhase phase = GoogleCatalogRefreshPhase.ProbeToken;
        void At(GoogleCatalogRefreshPhase next)
        {
            phase = next;
            GoogleCatalogRefreshDiagnostics.Phase(logger, refreshId, phase);
        }
        try
        {
            At(phase);
            ArgumentNullException.ThrowIfNull(account);
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            ArgumentNullException.ThrowIfNull(synthesisEndpoint);
            ValidateEndpointBinding(account, synthesisEndpoint);
            completionGate?.RequireProject(projectId);
            completionGate?.BeforeNetwork(account, synthesisEndpoint, true, refreshId);

            CredentialReference credential = account.CredentialReference
                ?? throw new InvalidOperationException("Google synthesis capability verification requires an admitted credential reference.");
            string accessToken = await credentialResolver
                .ResolveAccessTokenAsync(credential.TargetName, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Google synthesis capability verification did not receive a transient access token.");

            // Deliberately invalid and input-free: Google cannot synthesize audio from this payload.
            At(GoogleCatalogRefreshPhase.ProbeRequest);
            using HttpRequestMessage request = EmptyProbeRequest(synthesisEndpoint, accessToken, projectId);
            completionGate?.BeforeNetwork(account, synthesisEndpoint, true, refreshId);
            completionGate?.BeforeRequest(true);

            using HttpResponseMessage response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            GoogleCatalogRefreshDiagnostics.Http(logger, refreshId, phase, response.StatusCode);
            At(GoogleCatalogRefreshPhase.ProbeRead);
            byte[] body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);

            At(GoogleCatalogRefreshPhase.ProbeValidation);
            RequireValidationRejection(response.StatusCode, body, phase);

            DateTimeOffset capturedAtUtc = timeProvider.GetUtcNow();
            if (capturedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Google synthesis capability verification requires a UTC time provider.");
            string evidenceHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var evidence = new GoogleSynthesisCapabilityEvidence(
                capturedAtUtc,
                $"google-synthesis-probe:invalid-argument:sha256:{evidenceHash}");
            completionGate?.ProbeObserved(projectId, evidence);
            return evidence;
        }
        catch (Exception exception) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(exception))
        {
            GoogleCatalogRefreshDiagnostics.Failure(logger, refreshId, phase, exception);
            throw;
        }
    }

    private static HttpRequestMessage EmptyProbeRequest(Uri endpoint, string token, string project)
    {
        HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-goog-user-project", project.Trim());
        return request;
    }

    private static void RequireValidationRejection(HttpStatusCode status, byte[] body, GoogleCatalogRefreshPhase phase)
    {
        if (status != HttpStatusCode.BadRequest)
        {
            throw GoogleCatalogRefreshDiagnostics.WithHttpContext(new InvalidOperationException(
                $"Google synthesis capability probe was not admitted: expected an input-validation rejection but received HTTP {(int)status}."), status, phase);
        }

        string? providerStatus = TryReadGoogleErrorStatus(body);
        if (!string.Equals(providerStatus, "INVALID_ARGUMENT", StringComparison.Ordinal))
        {
            throw GoogleCatalogRefreshDiagnostics.WithHttpContext(new InvalidOperationException(
                "Google synthesis capability probe returned HTTP 400 without provider INVALID_ARGUMENT evidence."), status, phase);
        }
        using var inspected = JsonDocument.Parse(body);
        if (inspected.RootElement.TryGetProperty("audioContent", out _))
            throw new InvalidDataException("Input-free capability probe unexpectedly contained audio.");
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaximumEvidenceBodyBytes)
            throw new InvalidDataException("Google synthesis capability probe response exceeded the evidence size limit.");

        using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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

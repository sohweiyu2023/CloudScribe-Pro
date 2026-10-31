using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Loads the Google voice catalog from an explicitly configured endpoint using a
/// short-lived credential resolved at request time. The endpoint is never inferred
/// or hard-coded: it must share the already-admitted provider account origin.
/// </summary>
public sealed class GoogleVoiceCatalogClient(
    HttpClient httpClient,
    ITransientCredentialResolver credentialResolver,
    ILogger<GoogleVoiceCatalogClient>? logger = null, CompletionWorkflowGate? completionGate = null)
{
    public Task<GoogleVoiceCatalogSnapshot> LoadAsync(ProviderAccountReference account, Uri catalogEndpoint,
        CancellationToken cancellationToken = default) => LoadObservedAsync(account, catalogEndpoint, Guid.NewGuid(), cancellationToken);

    internal async Task<GoogleVoiceCatalogSnapshot> LoadObservedAsync(
        ProviderAccountReference account,
        Uri catalogEndpoint,
        Guid diagnosticRefreshId, CancellationToken cancellationToken)
    {
        Guid refreshId = completionGate?.CurrentRefreshId(diagnosticRefreshId) ?? diagnosticRefreshId;
        GoogleCatalogRefreshPhase phase = GoogleCatalogRefreshPhase.CatalogToken;
        void At(GoogleCatalogRefreshPhase next)
        {
            phase = next;
            GoogleCatalogRefreshDiagnostics.Phase(logger, refreshId, phase);
        }
        try
        {
            At(phase);
            ArgumentNullException.ThrowIfNull(account);
            ArgumentNullException.ThrowIfNull(catalogEndpoint);

            ValidateCatalogEndpoint(account, catalogEndpoint);
            completionGate?.BeforeNetwork(account, catalogEndpoint, false, refreshId);
            CredentialReference credentialReference = account.CredentialReference
                ?? throw new InvalidOperationException("Google voice discovery requires an admitted credential reference.");

            string accessToken = await credentialResolver
                .ResolveAccessTokenAsync(credentialReference.TargetName, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("Google voice discovery did not receive a usable transient access token.");
            }

            At(GoogleCatalogRefreshPhase.CatalogRequest);
            using HttpRequestMessage request = CatalogRequest(catalogEndpoint, accessToken);
            completionGate?.BeforeNetwork(account, catalogEndpoint, false, refreshId);
            completionGate?.BeforeRequest(false);

            using HttpResponseMessage response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            GoogleCatalogRefreshDiagnostics.Http(logger, refreshId, phase, response.StatusCode);
            At(GoogleCatalogRefreshPhase.CatalogRead);
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            RequireCatalogSuccess(response);

            At(GoogleCatalogRefreshPhase.CatalogParse);
            GoogleVoiceCatalogEntry[] voices = ParseVoices(body);
            if (voices.Length == 0)
            {
                throw new InvalidOperationException("Google returned a successful voice catalog response without any usable voices.");
            }

            var snapshot = CreateSnapshot(account, catalogEndpoint, body, voices);
            completionGate?.CatalogObserved(snapshot);
            return snapshot;
        }
        catch (Exception exception) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(exception))
        {
            GoogleCatalogRefreshDiagnostics.Failure(logger, refreshId, phase, exception);
            throw;
        }
    }

    private static void RequireCatalogSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google voice catalog request failed with HTTP {(int)response.StatusCode}.",
                inner: null, response.StatusCode);
    }

    private static HttpRequestMessage CatalogRequest(Uri endpoint, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static GoogleVoiceCatalogSnapshot CreateSnapshot(ProviderAccountReference account, Uri catalogEndpoint,
        byte[] body, GoogleVoiceCatalogEntry[] voices)
    {
        string contentHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return new GoogleVoiceCatalogSnapshot(
            account,
            DateTimeOffset.UtcNow,
            catalogEndpoint,
            $"google-voice-catalog:sha256:{contentHash}",
            voices);
    }

    private static GoogleVoiceCatalogEntry[] ParseVoices(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("voices", out JsonElement voicesElement)
            || voicesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Google voice catalog response is missing the voices array.");
        }

        List<GoogleVoiceCatalogEntry> voices = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonElement item in voicesElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                continue;
            }

            List<string> languageCodes = [];
            if (item.TryGetProperty("languageCodes", out JsonElement languageCodesElement)
                && languageCodesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement languageCodeElement in languageCodesElement.EnumerateArray())
                {
                    string? languageCode = languageCodeElement.ValueKind == JsonValueKind.String
                        ? languageCodeElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(languageCode))
                    {
                        languageCodes.Add(languageCode);
                    }
                }
            }

            string ssmlGender = item.TryGetProperty("ssmlGender", out JsonElement genderElement)
                && genderElement.ValueKind == JsonValueKind.String
                ? genderElement.GetString() ?? string.Empty
                : string.Empty;
            int naturalSampleRateHertz = item.TryGetProperty("naturalSampleRateHertz", out JsonElement rateElement)
                && rateElement.TryGetInt32(out int parsedRate)
                ? parsedRate
                : 0;

            voices.Add(new GoogleVoiceCatalogEntry(
                name,
                languageCodes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                ssmlGender,
                naturalSampleRateHertz));
        }

        return voices.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateCatalogEndpoint(ProviderAccountReference account, Uri catalogEndpoint)
    {
        if (!catalogEndpoint.IsAbsoluteUri
            || !string.Equals(catalogEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(catalogEndpoint.UserInfo)
            || !string.IsNullOrEmpty(catalogEndpoint.Fragment))
        {
            throw new InvalidOperationException("Google voice catalog endpoint must be an absolute HTTPS URI without credentials or a fragment.");
        }

        Uri admittedOrigin = account.EndpointOrigin
            ?? throw new InvalidOperationException("Google voice discovery requires an admitted provider endpoint origin.");
        Uri suppliedOrigin = new(catalogEndpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        if (Uri.Compare(
                admittedOrigin,
                suppliedOrigin,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException("Google voice catalog endpoint origin does not match the admitted provider account origin.");
        }
    }
}

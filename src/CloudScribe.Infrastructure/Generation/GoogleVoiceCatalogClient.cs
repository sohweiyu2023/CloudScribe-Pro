using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Loads the Google voice catalog from an explicitly configured endpoint using a
/// short-lived credential resolved at request time. The endpoint is never inferred
/// or hard-coded: it must share the already-admitted provider account origin.
/// </summary>
public sealed class GoogleVoiceCatalogClient(
    HttpClient httpClient,
    ITransientCredentialResolver credentialResolver)
{
    public async Task<GoogleVoiceCatalogSnapshot> LoadAsync(
        ProviderAccountReference account,
        Uri catalogEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(catalogEndpoint);

        ValidateCatalogEndpoint(account, catalogEndpoint);
        CredentialReference credentialReference = account.CredentialReference
            ?? throw new InvalidOperationException("Google voice discovery requires an admitted credential reference.");

        string accessToken = await credentialResolver
            .ResolveAccessTokenAsync(credentialReference.TargetName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Google voice discovery did not receive a usable transient access token.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, catalogEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Google voice catalog request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                inner: null,
                response.StatusCode);
        }

        IReadOnlyList<GoogleVoiceCatalogEntry> voices = ParseVoices(body);
        if (voices.Count == 0)
        {
            throw new InvalidOperationException("Google returned a successful voice catalog response without any usable voices.");
        }

        string contentHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return new GoogleVoiceCatalogSnapshot(
            account,
            DateTimeOffset.UtcNow,
            catalogEndpoint,
            $"google-voice-catalog:sha256:{contentHash}",
            voices);
    }

    private static IReadOnlyList<GoogleVoiceCatalogEntry> ParseVoices(byte[] json)
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

public sealed record GoogleVoiceCatalogSnapshot(
    ProviderAccountReference Account,
    DateTimeOffset ObservedAtUtc,
    Uri Endpoint,
    string ProvenanceId,
    IReadOnlyList<GoogleVoiceCatalogEntry> Voices)
{
    public IReadOnlySet<string> VoiceNames => Voices.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
    public IReadOnlySet<string> LanguageCodes => Voices
        .SelectMany(item => item.LanguageCodes)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record GoogleVoiceCatalogEntry(
    string Name,
    IReadOnlyList<string> LanguageCodes,
    string SsmlGender,
    int NaturalSampleRateHertz);

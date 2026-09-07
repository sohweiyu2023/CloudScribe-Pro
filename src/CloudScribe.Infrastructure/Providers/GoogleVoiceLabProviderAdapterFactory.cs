using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Configuration;

namespace CloudScribe.Infrastructure.Providers;

/// <summary>
/// Resolves the Google Voice Lab adapter from the exact persisted provider account.
/// The voice-catalog endpoint is configuration supplied and is still origin-pinned by
/// <see cref="GoogleVoiceCatalogClient"/>; no endpoint or authorization is inferred here.
/// </summary>
public sealed class GoogleVoiceLabProviderAdapterFactory(
    IProviderAccountStore accounts,
    GoogleVoiceCatalogClient catalogClient,
    IConfiguration configuration) : IProviderAdapterFactory
{
    internal const string CatalogEndpointConfigurationKey = "CloudScribe:GoogleTextToSpeech:VoiceCatalogEndpoint";

    private static readonly ProviderDescriptor GoogleDescriptor = new(
        GoogleGenerationProvider.StableProviderId,
        "Google Cloud Text-to-Speech",
        requiresNetwork: true,
        requiresCredentials: true);

    private readonly IProviderAccountStore _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly GoogleVoiceCatalogClient _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public ProviderDescriptor Descriptor => GoogleDescriptor;

    public async ValueTask<IProviderAdapter> CreateAdapterAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        ProviderAccountSnapshot account = await _accounts.FindAsync(
            Descriptor.StableId,
            accountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Google Voice Lab provider account is unavailable.");
        if (!account.IsEnabled)
            throw new InvalidOperationException("Google Voice Lab provider account is disabled.");
        if (account.Reference.CredentialReference is null)
            throw new InvalidOperationException("Google Voice Lab provider account has no credential reference.");
        if (account.Reference.EndpointOrigin is null)
            throw new InvalidOperationException("Google Voice Lab provider account has no admitted endpoint origin.");

        string endpointText = _configuration[CatalogEndpointConfigurationKey]
            ?? throw new InvalidOperationException(
                $"Google Voice Lab requires an explicitly configured '{CatalogEndpointConfigurationKey}'.");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint))
            throw new InvalidOperationException("Configured Google Voice Lab catalog endpoint is not an absolute URI.");

        return new GoogleVoiceLabProviderAdapter(account.Reference, endpoint, _catalogClient);
    }

    private sealed class GoogleVoiceLabProviderAdapter(
        ProviderAccountReference account,
        Uri catalogEndpoint,
        GoogleVoiceCatalogClient catalogClient) : IVoiceLabProviderAdapter
    {
        private readonly ProviderAccountReference _account = account ?? throw new ArgumentNullException(nameof(account));
        private readonly Uri _catalogEndpoint = catalogEndpoint ?? throw new ArgumentNullException(nameof(catalogEndpoint));
        private readonly GoogleVoiceCatalogClient _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));

        public ProviderDescriptor Descriptor => GoogleDescriptor;

        public async Task<IReadOnlyList<VoiceLabProviderCatalogVoice>> QueryVoiceLabCatalogAsync(
            VoiceLabProviderCatalogRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Validate();
            ValidateBinding(request);

            GoogleVoiceCatalogSnapshot snapshot = await _catalogClient
                .LoadAsync(_account, _catalogEndpoint, cancellationToken)
                .ConfigureAwait(false);

            IEnumerable<GoogleVoiceCatalogEntry> candidates = snapshot.Voices;
            if (!string.IsNullOrWhiteSpace(request.Locale))
            {
                candidates = candidates.Where(voice => voice.LanguageCodes.Any(
                    languageCode => LocaleMatches(languageCode, request.Locale)));
            }
            if (!string.IsNullOrWhiteSpace(request.SearchText))
            {
                candidates = candidates.Where(voice =>
                    voice.Name.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase) ||
                    voice.LanguageCodes.Any(languageCode =>
                        languageCode.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase)));
            }

            return candidates
                .Select(voice => new VoiceLabProviderCatalogVoice(
                    voice.Name,
                    CreateVoiceFingerprint(snapshot.ProvenanceId, voice),
                    VoiceEnabled: true,
                    // This does not invent project trust: QueryVoiceLabCatalogAsync is reachable in
                    // production only after VoiceLabProductionCatalogTransport has loaded and validated
                    // the exact persisted account/project/capability authorization, and it revalidates
                    // those bindings after this provider call before returning the selection.
                    AccountProjectAuthorized: true))
                .ToArray();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void ValidateBinding(VoiceLabProviderCatalogRequest request)
        {
            string expectedCredential = _account.CredentialReference?.TargetName
                ?? throw new InvalidOperationException("Google Voice Lab credential binding is unavailable.");
            if (!string.Equals(request.CredentialReferenceId, expectedCredential, StringComparison.Ordinal))
                throw new InvalidOperationException("Google Voice Lab credential binding changed before catalog execution.");

            Uri expectedOrigin = _account.EndpointOrigin
                ?? throw new InvalidOperationException("Google Voice Lab endpoint-origin binding is unavailable.");
            if (Uri.Compare(
                    request.EndpointOrigin,
                    expectedOrigin,
                    UriComponents.SchemeAndServer,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new InvalidOperationException("Google Voice Lab request origin does not match the persisted provider account.");
            }
        }

        private static bool LocaleMatches(string languageCode, string requestedLocale) =>
            string.Equals(languageCode, requestedLocale, StringComparison.OrdinalIgnoreCase) ||
            languageCode.StartsWith(requestedLocale + "-", StringComparison.OrdinalIgnoreCase) ||
            requestedLocale.StartsWith(languageCode + "-", StringComparison.OrdinalIgnoreCase);

        private static string CreateVoiceFingerprint(
            string provenanceId,
            GoogleVoiceCatalogEntry voice)
        {
            string canonical = string.Join("\n",
                provenanceId,
                voice.Name,
                string.Join(",", voice.LanguageCodes.Order(StringComparer.OrdinalIgnoreCase)),
                voice.SsmlGender,
                voice.NaturalSampleRateHertz.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return $"google-voice:sha256:{hash}";
        }
    }
}

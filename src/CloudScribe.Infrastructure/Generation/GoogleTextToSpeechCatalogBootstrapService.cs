using CloudScribe.Application.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Establishes the minimum persisted trust needed for Voice Lab from a fresh Google
/// service-account configuration. It proves only authenticated voice-catalog access for the
/// service account's owning project; it deliberately does not create synthesis/model or spend
/// authorization, which remain under the existing Stage6 fail-closed evidence chain.
/// </summary>
public sealed class GoogleTextToSpeechCatalogBootstrapService(
    GoogleServiceAccountCredentialOnboardingService credentialOnboarding,
    GoogleVoiceCatalogClient catalogClient,
    IProviderAccountStore accounts,
    IProviderCapabilitySnapshotStore capabilities,
    IVoiceLabProjectAuthorizationStore projectAuthorizations,
    TimeProvider timeProvider)
{
    public const string VoiceCatalogCapabilityId = "voice-catalog";
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(30);

    private readonly GoogleServiceAccountCredentialOnboardingService _credentialOnboarding =
        credentialOnboarding ?? throw new ArgumentNullException(nameof(credentialOnboarding));
    private readonly GoogleVoiceCatalogClient _catalogClient =
        catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
    private readonly IProviderAccountStore _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly IProviderCapabilitySnapshotStore _capabilities =
        capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IVoiceLabProjectAuthorizationStore _projectAuthorizations =
        projectAuthorizations ?? throw new ArgumentNullException(nameof(projectAuthorizations));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<GoogleTextToSpeechCatalogBootstrapResult> ConfigureFreshAsync(
        string accountId,
        string displayName,
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        Uri catalogEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReferenceId);
        ArgumentNullException.ThrowIfNull(catalogEndpoint);
        cancellationToken.ThrowIfCancellationRequested();

        ProviderAccountSnapshot? existing = await _accounts.FindAsync(
            GoogleGenerationProvider.StableProviderId,
            accountId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "Google TTS fresh configuration refuses to overwrite an existing provider account or credential binding.");
        }

        Uri endpointOrigin = GetHttpsOrigin(catalogEndpoint);
        bool credentialImported = false;
        try
        {
            GoogleServiceAccountCredentialIdentity identity = await _credentialOnboarding.ImportAsync(
                credentialReferenceId,
                serviceAccountJson,
                cancellationToken).ConfigureAwait(false);
            credentialImported = true;

            var accountReference = new ProviderAccountReference(
                GoogleGenerationProvider.StableProviderId,
                accountId,
                displayName,
                new CredentialReference(identity.CredentialReferenceId),
                endpointOrigin: endpointOrigin);

            // This direct bootstrap observation is intentionally narrower than Stage6. The real
            // authenticated response must succeed before any account/catalog evidence is persisted.
            GoogleVoiceCatalogSnapshot observedCatalog = await _catalogClient.LoadAsync(
                accountReference,
                catalogEndpoint,
                cancellationToken).ConfigureAwait(false);

            ProviderAccountSnapshot storedAccount = await _accounts.CreateAsync(
                accountReference,
                isEnabled: true,
                cancellationToken).ConfigureAwait(false);

            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            if (nowUtc.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Google catalog bootstrap requires a UTC time provider.");
            DateTimeOffset capturedAtUtc = observedCatalog.ObservedAtUtc.ToUniversalTime();
            if (capturedAtUtc > nowUtc.AddMinutes(1))
                throw new InvalidOperationException("Google catalog observation timestamp is unexpectedly in the future.");
            DateTimeOffset expiresAtUtc = nowUtc.Add(EvidenceLifetime);

            var capabilitySnapshot = new ProviderCapabilitySnapshot(
                storedAccount.Reference,
                capturedAtUtc,
                observedCatalog.ProvenanceId,
                [new ProviderCapability(
                    VoiceCatalogCapabilityId,
                    ProviderCapabilityState.Supported,
                    ProviderLifecycleState.Available)]);
            StoredProviderCapabilitySnapshot storedCapability = await _capabilities.SaveAsync(
                capabilitySnapshot,
                expiresAtUtc,
                cancellationToken).ConfigureAwait(false);

            var projectEvidence = new VoiceLabProjectAuthorizationEvidence(
                GoogleGenerationProvider.StableProviderId,
                storedAccount.Reference.AccountId,
                identity.ProjectId,
                storedAccount.Revision,
                identity.CredentialReferenceId,
                storedCapability.Id.ToString("D"),
                ProjectAuthorized: true,
                PrivateVoiceAccessAuthorized: false,
                CapturedAtUtc: nowUtc,
                ExpiresAtUtc: expiresAtUtc);
            await _projectAuthorizations.SaveVerifiedAsync(projectEvidence, cancellationToken).ConfigureAwait(false);

            return new GoogleTextToSpeechCatalogBootstrapResult(
                storedAccount,
                storedCapability,
                identity.ProjectId,
                observedCatalog.ProvenanceId,
                observedCatalog.Voices,
                expiresAtUtc);
        }
        catch
        {
            // Before provider-account persistence, a failed authenticated observation must not leave
            // newly imported credential material behind. Once an account is persisted, its exact
            // binding is retained so the failure is visible and retryable rather than silently
            // deleting credentials underneath durable state.
            ProviderAccountSnapshot? persisted = await _accounts.FindAsync(
                GoogleGenerationProvider.StableProviderId,
                accountId,
                CancellationToken.None).ConfigureAwait(false);
            if (credentialImported && persisted is null)
            {
                _ = await _credentialOnboarding.RemoveAsync(
                    credentialReferenceId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static Uri GetHttpsOrigin(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "Google voice catalog endpoint must be an absolute HTTPS URI without credentials or a fragment.",
                nameof(endpoint));
        }
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }
}

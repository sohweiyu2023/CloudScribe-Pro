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
        ValidateInputs(accountId, displayName, credentialReferenceId, catalogEndpoint);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireFreshAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        // Admit the destination before importing or resolving any credential. A user-supplied
        // HTTPS URI must never be able to self-authorize an arbitrary bearer-token destination.
        Uri endpointOrigin = GetGoogleApiOrigin(catalogEndpoint);
        bool credentialImported = false;
        try
        {
            GoogleServiceAccountCredentialIdentity identity = await _credentialOnboarding.ImportAsync(
                credentialReferenceId,
                serviceAccountJson,
                catalogEndpoint,
                cancellationToken).ConfigureAwait(false);
            credentialImported = true;
            return await ObserveAndPersistAsync(
                accountId,
                displayName,
                catalogEndpoint,
                endpointOrigin,
                identity,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveUnboundCredentialAfterFailureAsync(
                accountId,
                credentialReferenceId,
                credentialImported).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<GoogleTextToSpeechCatalogBootstrapResult> ObserveAndPersistAsync(
        string accountId,
        string displayName,
        Uri catalogEndpoint,
        Uri endpointOrigin,
        GoogleServiceAccountCredentialIdentity identity,
        CancellationToken cancellationToken)
    {
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

        EvidenceWindow window = CreateEvidenceWindow(observedCatalog);
        StoredProviderCapabilitySnapshot storedCapability = await PersistCatalogCapabilityAsync(
            storedAccount,
            observedCatalog,
            window,
            cancellationToken).ConfigureAwait(false);
        await PersistProjectEvidenceAsync(
            storedAccount,
            storedCapability,
            identity,
            window,
            cancellationToken).ConfigureAwait(false);

        return new GoogleTextToSpeechCatalogBootstrapResult(
            storedAccount,
            storedCapability,
            identity.ProjectId,
            observedCatalog.ProvenanceId,
            observedCatalog.Voices,
            window.ExpiresAtUtc);
    }

    private async Task<StoredProviderCapabilitySnapshot> PersistCatalogCapabilityAsync(
        ProviderAccountSnapshot storedAccount,
        GoogleVoiceCatalogSnapshot observedCatalog,
        EvidenceWindow window,
        CancellationToken cancellationToken)
    {
        var capabilitySnapshot = new ProviderCapabilitySnapshot(
            storedAccount.Reference,
            window.CapturedAtUtc,
            observedCatalog.ProvenanceId,
            [new ProviderCapability(
                VoiceCatalogCapabilityId,
                ProviderCapabilityState.Supported,
                ProviderLifecycleState.Available)]);
        return await _capabilities.SaveAsync(
            capabilitySnapshot,
            window.ExpiresAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistProjectEvidenceAsync(
        ProviderAccountSnapshot storedAccount,
        StoredProviderCapabilitySnapshot storedCapability,
        GoogleServiceAccountCredentialIdentity identity,
        EvidenceWindow window,
        CancellationToken cancellationToken)
    {
        var projectEvidence = new VoiceLabProjectAuthorizationEvidence(
            GoogleGenerationProvider.StableProviderId,
            storedAccount.Reference.AccountId,
            identity.ProjectId,
            storedAccount.Revision,
            identity.CredentialReferenceId,
            storedCapability.Id.ToString("D"),
            ProjectAuthorized: true,
            PrivateVoiceAccessAuthorized: false,
            CapturedAtUtc: window.NowUtc,
            ExpiresAtUtc: window.ExpiresAtUtc);
        await _projectAuthorizations.SaveVerifiedAsync(projectEvidence, cancellationToken).ConfigureAwait(false);
    }

    private EvidenceWindow CreateEvidenceWindow(GoogleVoiceCatalogSnapshot observedCatalog)
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Google catalog bootstrap requires a UTC time provider.");
        DateTimeOffset capturedAtUtc = observedCatalog.ObservedAtUtc.ToUniversalTime();
        if (capturedAtUtc > nowUtc.AddMinutes(1))
            throw new InvalidOperationException("Google catalog observation timestamp is unexpectedly in the future.");
        return new EvidenceWindow(nowUtc, capturedAtUtc, nowUtc.Add(EvidenceLifetime));
    }

    private async Task RequireFreshAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        ProviderAccountSnapshot? existing = await _accounts.FindAsync(
            GoogleGenerationProvider.StableProviderId,
            accountId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "Google TTS fresh configuration refuses to overwrite an existing provider account or credential binding.");
        }
    }

    private async Task RemoveUnboundCredentialAfterFailureAsync(
        string accountId,
        string credentialReferenceId,
        bool credentialImported)
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
    }

    private static void ValidateInputs(
        string accountId,
        string displayName,
        string credentialReferenceId,
        Uri catalogEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReferenceId);
        ArgumentNullException.ThrowIfNull(catalogEndpoint);
    }

    private static Uri GetGoogleApiOrigin(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !endpoint.IsDefaultPort ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !IsGoogleApisHost(endpoint.Host))
        {
            throw new ArgumentException(
                "Google voice catalog endpoint must be a credential-free absolute HTTPS Google APIs URI on the default port without a fragment.",
                nameof(endpoint));
        }
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static bool IsGoogleApisHost(string host) =>
        string.Equals(host, "googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase);

    private sealed record EvidenceWindow(
        DateTimeOffset NowUtc,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}

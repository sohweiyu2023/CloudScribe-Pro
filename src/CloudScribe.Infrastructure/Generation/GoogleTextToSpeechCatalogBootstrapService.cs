using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Establishes persisted Google TTS evidence for a fresh service-account configuration only after
/// both a real authenticated voice-catalog read and an authenticated non-billable synthesis
/// capability probe succeed. The resulting capability provenance is derived from those observed
/// provider responses; no capability or project/model authorization is fabricated locally.
/// </summary>
public sealed class GoogleTextToSpeechCatalogBootstrapService(
    GoogleServiceAccountCredentialOnboardingService credentialOnboarding,
    GoogleVoiceCatalogClient catalogClient,
    GoogleSynthesisCapabilityProbe synthesisProbe,
    IProviderAccountStore accounts,
    IProviderCapabilitySnapshotStore capabilities,
    IVoiceLabProjectAuthorizationStore projectAuthorizations,
    IGoogleGenerationProjectAuthorizationStore generationProjectAuthorizations,
    TimeProvider timeProvider)
{
    public const string VoiceCatalogCapabilityId = "voice-catalog";
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(30);

    private readonly GoogleServiceAccountCredentialOnboardingService _credentialOnboarding = credentialOnboarding ?? throw new ArgumentNullException(nameof(credentialOnboarding));
    private readonly GoogleVoiceCatalogClient _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
    private readonly GoogleSynthesisCapabilityProbe _synthesisProbe = synthesisProbe ?? throw new ArgumentNullException(nameof(synthesisProbe));
    private readonly IProviderAccountStore _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly IProviderCapabilitySnapshotStore _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IVoiceLabProjectAuthorizationStore _projectAuthorizations = projectAuthorizations ?? throw new ArgumentNullException(nameof(projectAuthorizations));
    private readonly IGoogleGenerationProjectAuthorizationStore _generationProjectAuthorizations = generationProjectAuthorizations ?? throw new ArgumentNullException(nameof(generationProjectAuthorizations));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<GoogleTextToSpeechCatalogBootstrapResult> ConfigureFreshAsync(
        string accountId,
        string displayName,
        string credentialReferenceId,
        ReadOnlyMemory<char> serviceAccountJson,
        Uri catalogEndpoint,
        Uri synthesisEndpoint,
        string regionId,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(accountId, displayName, credentialReferenceId, catalogEndpoint, synthesisEndpoint, regionId);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireFreshAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        Uri catalogOrigin = GetGoogleApiOrigin(catalogEndpoint);
        Uri synthesisOrigin = GetGoogleApiOrigin(synthesisEndpoint);
        RequireSameOrigin(catalogOrigin, synthesisOrigin);
        bool credentialImported = false;
        try
        {
            GoogleServiceAccountCredentialIdentity identity = await _credentialOnboarding.ImportAsync(
                credentialReferenceId,
                serviceAccountJson,
                catalogEndpoint,
                synthesisEndpoint,
                cancellationToken).ConfigureAwait(false);
            credentialImported = true;
            return await ObserveAndPersistAsync(
                accountId,
                displayName,
                catalogEndpoint,
                synthesisEndpoint,
                synthesisOrigin,
                regionId,
                identity,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveUnboundCredentialAfterFailureAsync(accountId, credentialReferenceId, credentialImported).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<GoogleTextToSpeechCatalogBootstrapResult> ObserveAndPersistAsync(
        string accountId,
        string displayName,
        Uri catalogEndpoint,
        Uri synthesisEndpoint,
        Uri synthesisOrigin,
        string regionId,
        GoogleServiceAccountCredentialIdentity identity,
        CancellationToken cancellationToken)
    {
        string endpointId = GoogleTextToSpeechEndpointIdentity.Create(synthesisEndpoint);
        var accountReference = new ProviderAccountReference(
            GoogleGenerationProvider.StableProviderId,
            accountId,
            displayName,
            new CredentialReference(identity.CredentialReferenceId),
            endpointId: endpointId,
            regionId: regionId,
            endpointOrigin: synthesisOrigin);

        GoogleVoiceCatalogSnapshot observedCatalog = await _catalogClient.LoadAsync(
            accountReference,
            catalogEndpoint,
            cancellationToken).ConfigureAwait(false);
        GoogleSynthesisCapabilityEvidence synthesisEvidence = await _synthesisProbe.VerifyAsync(
            accountReference,
            identity.ProjectId,
            synthesisEndpoint,
            cancellationToken).ConfigureAwait(false);

        ProviderAccountSnapshot storedAccount = await _accounts.CreateAsync(
            accountReference,
            isEnabled: true,
            cancellationToken).ConfigureAwait(false);

        EvidenceWindow window = CreateEvidenceWindow(observedCatalog, synthesisEvidence);
        string capabilityProvenance = BuildCombinedCapabilityProvenance(observedCatalog.ProvenanceId, synthesisEvidence.ProvenanceId);
        StoredProviderCapabilitySnapshot storedCapability = await PersistCapabilitiesAsync(
            storedAccount,
            capabilityProvenance,
            window,
            cancellationToken).ConfigureAwait(false);
        await PersistVoiceLabProjectEvidenceAsync(storedAccount, storedCapability, identity, window, cancellationToken).ConfigureAwait(false);
        await PersistGenerationVoiceAuthorizationsAsync(
            storedAccount,
            storedCapability.Snapshot.ProvenanceId,
            identity,
            observedCatalog.Voices,
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

    private async Task<StoredProviderCapabilitySnapshot> PersistCapabilitiesAsync(
        ProviderAccountSnapshot storedAccount,
        string provenanceId,
        EvidenceWindow window,
        CancellationToken cancellationToken)
    {
        var capabilitySnapshot = new ProviderCapabilitySnapshot(
            storedAccount.Reference,
            window.CapturedAtUtc,
            provenanceId,
            [
                new ProviderCapability(VoiceCatalogCapabilityId, ProviderCapabilityState.Supported, ProviderLifecycleState.Available),
                new ProviderCapability(GoogleGenerationProvider.SynthesizeOperationStableId, ProviderCapabilityState.Supported, ProviderLifecycleState.Available),
            ]);
        return await _capabilities.SaveAsync(capabilitySnapshot, window.ExpiresAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistVoiceLabProjectEvidenceAsync(
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

    private async Task PersistGenerationVoiceAuthorizationsAsync(
        ProviderAccountSnapshot storedAccount,
        string capabilityProvenanceId,
        GoogleServiceAccountCredentialIdentity identity,
        IReadOnlyList<GoogleVoiceCatalogEntry> voices,
        EvidenceWindow window,
        CancellationToken cancellationToken)
    {
        ProviderAccountReference account = storedAccount.Reference;
        string endpointId = account.EndpointId ?? throw new InvalidOperationException("Google synthesis endpoint identity was not persisted.");
        string regionId = account.RegionId ?? throw new InvalidOperationException("Google region identity was not persisted.");
        string endpointOrigin = account.EndpointOrigin?.GetLeftPart(UriPartial.Authority)
            ?? throw new InvalidOperationException("Google synthesis endpoint origin was not persisted.");

        foreach (GoogleVoiceCatalogEntry voice in voices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = new GoogleGenerationProjectAuthorizationEvidence(
                account.AccountId,
                identity.ProjectId,
                voice.Name,
                identity.CredentialReferenceId,
                capabilityProvenanceId,
                endpointId,
                regionId,
                endpointOrigin,
                Authorized: true,
                CapturedAtUtc: window.NowUtc,
                ExpiresAtUtc: window.ExpiresAtUtc);
            await _generationProjectAuthorizations.SaveVerifiedAsync(evidence, cancellationToken).ConfigureAwait(false);
        }
    }

    private EvidenceWindow CreateEvidenceWindow(
        GoogleVoiceCatalogSnapshot observedCatalog,
        GoogleSynthesisCapabilityEvidence synthesisEvidence)
    {
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Google catalog bootstrap requires a UTC time provider.");
        DateTimeOffset catalogCaptured = observedCatalog.ObservedAtUtc.ToUniversalTime();
        DateTimeOffset synthesisCaptured = synthesisEvidence.CapturedAtUtc.ToUniversalTime();
        if (catalogCaptured > nowUtc.AddMinutes(1) || synthesisCaptured > nowUtc.AddMinutes(1))
            throw new InvalidOperationException("Google onboarding observation timestamp is unexpectedly in the future.");
        DateTimeOffset capturedAtUtc = catalogCaptured > synthesisCaptured ? catalogCaptured : synthesisCaptured;
        return new EvidenceWindow(nowUtc, capturedAtUtc, nowUtc.Add(EvidenceLifetime));
    }

    private static string BuildCombinedCapabilityProvenance(string catalogProvenanceId, string synthesisProvenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(synthesisProvenanceId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{catalogProvenanceId}\n{synthesisProvenanceId}"));
        return $"google-tts-capability-v1:sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task RequireFreshAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        ProviderAccountSnapshot? existing = await _accounts.FindAsync(GoogleGenerationProvider.StableProviderId, accountId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            throw new InvalidOperationException("Google TTS fresh configuration refuses to overwrite an existing provider account or credential binding.");
    }

    private async Task RemoveUnboundCredentialAfterFailureAsync(string accountId, string credentialReferenceId, bool credentialImported)
    {
        ProviderAccountSnapshot? persisted = await _accounts.FindAsync(
            GoogleGenerationProvider.StableProviderId,
            accountId,
            CancellationToken.None).ConfigureAwait(false);
        if (credentialImported && persisted is null)
            _ = await _credentialOnboarding.RemoveAsync(credentialReferenceId, CancellationToken.None).ConfigureAwait(false);
    }

    private static void ValidateInputs(
        string accountId,
        string displayName,
        string credentialReferenceId,
        Uri catalogEndpoint,
        Uri synthesisEndpoint,
        string regionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReferenceId);
        ArgumentNullException.ThrowIfNull(catalogEndpoint);
        ArgumentNullException.ThrowIfNull(synthesisEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);
    }

    private static Uri GetGoogleApiOrigin(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !endpoint.IsDefaultPort
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !IsGoogleApisHost(endpoint.Host))
        {
            throw new ArgumentException(
                "Google endpoint must be a credential-free absolute HTTPS Google APIs URI on the default port without a fragment.",
                nameof(endpoint));
        }
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static void RequireSameOrigin(Uri catalogOrigin, Uri synthesisOrigin)
    {
        if (Uri.Compare(catalogOrigin, synthesisOrigin, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw new ArgumentException("Google voice-catalog and synthesis endpoints must use the same admitted Google API origin.", nameof(synthesisOrigin));
    }

    private static bool IsGoogleApisHost(string host) =>
        string.Equals(host, "googleapis.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase);

    private sealed record EvidenceWindow(DateTimeOffset NowUtc, DateTimeOffset CapturedAtUtc, DateTimeOffset ExpiresAtUtc);
}

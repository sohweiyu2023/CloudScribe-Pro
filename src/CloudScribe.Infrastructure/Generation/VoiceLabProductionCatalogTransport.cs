using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabProductionCatalogTransport
{
    private const int MaxCatalogResults = 500;
    private readonly IProviderAccountStore _accounts;
    private readonly IProviderCapabilitySnapshotStore _capabilities;
    private readonly Func<VoiceLabCatalogQuery, CancellationToken, Task<VoiceLabCatalogAuthorizationEvidence?>> _loadAuthorizationAsync;
    private readonly Func<VoiceLabCatalogTransportContext, CancellationToken, Task<IReadOnlyList<VoiceLabCatalogSelection>>> _queryProviderAsync;
    private readonly TimeProvider _timeProvider;

    public VoiceLabProductionCatalogTransport(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        Func<VoiceLabCatalogQuery, CancellationToken, Task<VoiceLabCatalogAuthorizationEvidence?>> loadAuthorizationAsync,
        Func<VoiceLabCatalogTransportContext, CancellationToken, Task<IReadOnlyList<VoiceLabCatalogSelection>>> queryProviderAsync,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _loadAuthorizationAsync = loadAuthorizationAsync ?? throw new ArgumentNullException(nameof(loadAuthorizationAsync));
        _queryProviderAsync = queryProviderAsync ?? throw new ArgumentNullException(nameof(queryProviderAsync));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<VoiceLabCatalogSelection>> QueryAsync(
        VoiceLabCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateQueryShape(query);

        VoiceLabCatalogAuthorizationEvidence evidence = await LoadAuthorizationAsync(query, cancellationToken).ConfigureAwait(false);
        ProviderAccountSnapshot account = await LoadAccountAsync(query, cancellationToken).ConfigureAwait(false);
        VoiceLabCatalogQueryPolicy.RequireAuthorized(
            query,
            account.IsEnabled,
            evidence.ProjectAuthorized,
            evidence.PrivateVoiceAccessAuthorized);

        string credentialReferenceId = ValidateAccountBinding(account, evidence);
        StoredProviderCapabilitySnapshot capability = await LoadCapabilityAsync(query, cancellationToken).ConfigureAwait(false);
        string capabilityEvidenceId = ValidateCapabilityBinding(capability, evidence);
        Uri endpointOrigin = account.Reference.EndpointOrigin
            ?? throw new InvalidOperationException("Voice Lab catalog provider account has no explicit endpoint origin.");

        IReadOnlyList<VoiceLabCatalogSelection> results = await QueryProviderAsync(
            query,
            credentialReferenceId,
            capabilityEvidenceId,
            endpointOrigin,
            cancellationToken).ConfigureAwait(false);
        ValidateResults(results, query, capabilityEvidenceId, cancellationToken);
        return results;
    }

    private async Task<VoiceLabCatalogAuthorizationEvidence> LoadAuthorizationAsync(
        VoiceLabCatalogQuery query,
        CancellationToken cancellationToken)
    {
        VoiceLabCatalogAuthorizationEvidence evidence = await _loadAuthorizationAsync(query, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog current authorization evidence is unavailable.");
        return evidence.Validate(query);
    }

    private async Task<ProviderAccountSnapshot> LoadAccountAsync(
        VoiceLabCatalogQuery query,
        CancellationToken cancellationToken)
    {
        return await _accounts.FindAsync(
            query.ProviderId,
            query.AccountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog provider account is no longer available.");
    }

    private async Task<StoredProviderCapabilitySnapshot> LoadCapabilityAsync(
        VoiceLabCatalogQuery query,
        CancellationToken cancellationToken)
    {
        StoredProviderCapabilitySnapshot capability = await _capabilities.GetLatestAsync(
            query.ProviderId,
            query.AccountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog capability evidence is unavailable.");
        if (capability.IsStale(_timeProvider.GetUtcNow()))
            throw new InvalidOperationException("Voice Lab catalog capability evidence is stale.");
        return capability;
    }

    private static string ValidateAccountBinding(
        ProviderAccountSnapshot account,
        VoiceLabCatalogAuthorizationEvidence evidence)
    {
        if (account.Revision != evidence.AccountRevision)
            throw new InvalidOperationException("Voice Lab catalog provider account revision changed after authorization.");

        string credentialReferenceId = account.Reference.CredentialReference?.TargetName
            ?? throw new InvalidOperationException("Voice Lab catalog provider account has no credential reference.");
        if (!string.Equals(credentialReferenceId, evidence.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab catalog credential reference changed after authorization.");
        return credentialReferenceId;
    }

    private static string ValidateCapabilityBinding(
        StoredProviderCapabilitySnapshot capability,
        VoiceLabCatalogAuthorizationEvidence evidence)
    {
        string capabilityEvidenceId = capability.Id.ToString("D");
        if (!string.Equals(capabilityEvidenceId, evidence.CapabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Voice Lab catalog capability evidence changed after authorization.");
        return capabilityEvidenceId;
    }

    private async Task<IReadOnlyList<VoiceLabCatalogSelection>> QueryProviderAsync(
        VoiceLabCatalogQuery query,
        string credentialReferenceId,
        string capabilityEvidenceId,
        Uri endpointOrigin,
        CancellationToken cancellationToken)
    {
        var context = new VoiceLabCatalogTransportContext(query, credentialReferenceId, capabilityEvidenceId, endpointOrigin);
        IReadOnlyList<VoiceLabCatalogSelection> results = await _queryProviderAsync(context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog provider transport returned no result collection.");
        if (results.Count > MaxCatalogResults)
            throw new InvalidOperationException($"Voice Lab catalog provider transport returned {results.Count} results; maximum is {MaxCatalogResults}.");
        return results;
    }

    private static void ValidateResults(
        IReadOnlyList<VoiceLabCatalogSelection> results,
        VoiceLabCatalogQuery query,
        string capabilityEvidenceId,
        CancellationToken cancellationToken)
    {
        var seenVoiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (VoiceLabCatalogSelection selection in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selection.Validate();
            RequireSelectionBinding(selection, query, capabilityEvidenceId);
            if (!seenVoiceIds.Add(selection.VoiceStableId))
                throw new InvalidOperationException("Voice Lab catalog provider returned duplicate voice identities.");
        }
    }

    private static void RequireSelectionBinding(
        VoiceLabCatalogSelection selection,
        VoiceLabCatalogQuery query,
        string capabilityEvidenceId)
    {
        if (!string.Equals(selection.ProviderStableId, query.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(selection.AccountStableId, query.AccountId, StringComparison.Ordinal) ||
            !string.Equals(selection.ProjectStableId, query.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(selection.CapabilityEvidenceId, capabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voice Lab catalog provider returned a selection outside the current persisted trust boundary.");
        }
    }

    private static void ValidateQueryShape(VoiceLabCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (string value in new[] { query.ProviderId, query.AccountId, query.ProjectId })
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            {
                throw new InvalidOperationException("Voice Lab catalog query contains a non-canonical trust identity.");
            }
        }
        if (query.SearchText is { Length: > 256 })
            throw new InvalidOperationException("Voice Lab search text exceeds the bounded query length.");
        if (query.Locale is { Length: > 32 })
            throw new InvalidOperationException("Voice Lab locale filter exceeds the bounded length.");
    }
}

using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record VoiceLabCatalogAuthorizationEvidence(
    string ProviderId,
    string AccountId,
    string ProjectId,
    long AccountRevision,
    string CredentialReferenceId,
    string CapabilityEvidenceId,
    bool ProjectAuthorized,
    bool PrivateVoiceAccessAuthorized)
{
    public VoiceLabCatalogAuthorizationEvidence Validate(VoiceLabCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!string.Equals(ProviderId, query.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(AccountId, query.AccountId, StringComparison.Ordinal) ||
            !string.Equals(ProjectId, query.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Voice Lab catalog authorization evidence is bound to a different provider/account/project request.");
        }
        if (AccountRevision < 1)
            throw new InvalidOperationException("Voice Lab catalog authorization evidence requires a persisted account revision.");
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(CapabilityEvidenceId, nameof(CapabilityEvidenceId));
        if (!ProjectAuthorized)
            throw new InvalidOperationException("Voice Lab catalog project authorization is no longer current.");
        if (query.IncludePrivateVoices && !PrivateVoiceAccessAuthorized)
            throw new InvalidOperationException("Voice Lab private voice access authorization is no longer current.");
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Voice Lab catalog evidence '{parameterName}' is not canonical.");
        }
    }
}

public sealed record VoiceLabCatalogTransportContext(
    VoiceLabCatalogQuery Query,
    string CredentialReferenceId,
    string CapabilityEvidenceId,
    Uri EndpointOrigin);

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

        VoiceLabCatalogAuthorizationEvidence evidence = await _loadAuthorizationAsync(query, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog current authorization evidence is unavailable.");
        evidence.Validate(query);

        ProviderAccountSnapshot account = await _accounts.FindAsync(
            query.ProviderId,
            query.AccountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog provider account is no longer available.");

        VoiceLabCatalogQueryPolicy.RequireAuthorized(
            query,
            account.IsEnabled,
            evidence.ProjectAuthorized,
            evidence.PrivateVoiceAccessAuthorized);
        if (account.Revision != evidence.AccountRevision)
            throw new InvalidOperationException("Voice Lab catalog provider account revision changed after authorization.");

        string credentialReferenceId = account.Reference.CredentialReference?.TargetName
            ?? throw new InvalidOperationException("Voice Lab catalog provider account has no credential reference.");
        if (!string.Equals(credentialReferenceId, evidence.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab catalog credential reference changed after authorization.");

        Uri endpointOrigin = account.Reference.EndpointOrigin
            ?? throw new InvalidOperationException("Voice Lab catalog provider account has no explicit endpoint origin.");

        StoredProviderCapabilitySnapshot capability = await _capabilities.GetLatestAsync(
            query.ProviderId,
            query.AccountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog capability evidence is unavailable.");
        if (capability.IsStale(_timeProvider.GetUtcNow()))
            throw new InvalidOperationException("Voice Lab catalog capability evidence is stale.");

        string capabilityEvidenceId = capability.Id.ToString("D");
        if (!string.Equals(capabilityEvidenceId, evidence.CapabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Voice Lab catalog capability evidence changed after authorization.");

        var context = new VoiceLabCatalogTransportContext(query, credentialReferenceId, capabilityEvidenceId, endpointOrigin);
        IReadOnlyList<VoiceLabCatalogSelection> results = await _queryProviderAsync(context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog provider transport returned no result collection.");
        if (results.Count > MaxCatalogResults)
            throw new InvalidOperationException($"Voice Lab catalog provider transport returned {results.Count} results; maximum is {MaxCatalogResults}.");

        var seenVoiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (VoiceLabCatalogSelection selection in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selection.Validate();
            if (!string.Equals(selection.ProviderStableId, query.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(selection.AccountStableId, query.AccountId, StringComparison.Ordinal) ||
                !string.Equals(selection.ProjectStableId, query.ProjectId, StringComparison.Ordinal) ||
                !string.Equals(selection.CapabilityEvidenceId, capabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voice Lab catalog provider returned a selection outside the current persisted trust boundary.");
            }
            if (!seenVoiceIds.Add(selection.VoiceStableId))
                throw new InvalidOperationException("Voice Lab catalog provider returned duplicate voice identities.");
        }

        return results;
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

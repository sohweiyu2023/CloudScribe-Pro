using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabCatalogCurrentEvidenceResolver
{
    private readonly IProviderAccountStore _accounts;
    private readonly IProviderCapabilitySnapshotStore _capabilities;
    private readonly IVoiceLabProjectAuthorizationStore _projects;
    private readonly TimeProvider _timeProvider;

    public VoiceLabCatalogCurrentEvidenceResolver(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        IVoiceLabProjectAuthorizationStore projects,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<VoiceLabCatalogAuthorizationEvidence?> ResolveAsync(
        VoiceLabCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        ProviderAccountSnapshot? account = await _accounts.FindAsync(
            query.ProviderId,
            query.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (account is null || !account.IsEnabled)
            return null;

        string? credentialReferenceId = account.Reference.CredentialReference?.TargetName;
        if (string.IsNullOrWhiteSpace(credentialReferenceId))
            return null;

        StoredProviderCapabilitySnapshot? capability = await _capabilities.GetLatestAsync(
            query.ProviderId,
            query.AccountId,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (capability is null || capability.IsStale(nowUtc))
            return null;

        if (!string.Equals(capability.Snapshot.Account.ProviderStableId, query.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(capability.Snapshot.Account.AccountId, query.AccountId, StringComparison.Ordinal) ||
            !string.Equals(capability.Snapshot.Account.CredentialReference?.TargetName, credentialReferenceId, StringComparison.Ordinal))
        {
            return null;
        }

        VoiceLabProjectAuthorizationEvidence? project = await _projects.LoadCurrentAsync(
            query.ProviderId,
            query.AccountId,
            query.ProjectId,
            cancellationToken).ConfigureAwait(false);
        if (project is null || !project.IsCurrent(nowUtc))
            return null;

        string capabilityEvidenceId = capability.Id.ToString("D");
        if (project.AccountRevision != account.Revision ||
            !string.Equals(project.CredentialReferenceId, credentialReferenceId, StringComparison.Ordinal) ||
            !string.Equals(project.CapabilityEvidenceId, capabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (query.IncludePrivateVoices && !project.PrivateVoiceAccessAuthorized)
            return null;

        return new VoiceLabCatalogAuthorizationEvidence(
            query.ProviderId,
            query.AccountId,
            query.ProjectId,
            account.Revision,
            credentialReferenceId,
            capabilityEvidenceId,
            ProjectAuthorized: true,
            PrivateVoiceAccessAuthorized: project.PrivateVoiceAccessAuthorized);
    }
}

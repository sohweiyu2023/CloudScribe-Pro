using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabCatalogCurrentEvidenceResolver
{
    private readonly IProviderAccountStore _accounts;
    private readonly IProviderCapabilitySnapshotStore _capabilities;
    private readonly TimeProvider _timeProvider;

    public VoiceLabCatalogCurrentEvidenceResolver(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
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
        if (capability is null || capability.IsStale(_timeProvider.GetUtcNow()))
            return null;

        if (!string.Equals(capability.Snapshot.Account.ProviderStableId, query.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(capability.Snapshot.Account.AccountId, query.AccountId, StringComparison.Ordinal) ||
            !string.Equals(capability.Snapshot.Account.CredentialReference?.TargetName, credentialReferenceId, StringComparison.Ordinal))
        {
            return null;
        }

        // Persisted account/capability state can prove provider/account/credential/capability
        // freshness, but it does not by itself prove project membership or private-voice
        // entitlement. Those claims remain fail-closed until a production project-access
        // evidence source is composed.
        return new VoiceLabCatalogAuthorizationEvidence(
            query.ProviderId,
            query.AccountId,
            query.ProjectId,
            account.Revision,
            credentialReferenceId,
            capability.Id.ToString("D"),
            ProjectAuthorized: false,
            PrivateVoiceAccessAuthorized: false);
    }
}

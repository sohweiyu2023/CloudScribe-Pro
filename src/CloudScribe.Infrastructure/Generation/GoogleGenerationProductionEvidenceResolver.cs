using CloudScribe.Application.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationProductionEvidence(
    ProviderAccountSnapshot Account,
    StoredProviderCapabilitySnapshot Capability)
{
    public GoogleGenerationProductionEvidence Validate(DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(Account);
        ArgumentNullException.ThrowIfNull(Capability);

        ProviderAccountReference account = Account.Reference;
        ProviderCapabilitySnapshot capability = Capability.Snapshot;

        if (!string.Equals(account.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Current provider account is not the Google generation provider.");
        if (!Account.IsEnabled)
            throw new InvalidOperationException("Current Google provider account is disabled.");
        if (account.CredentialReference is null)
            throw new InvalidOperationException("Current Google provider account has no credential reference.");
        if (string.IsNullOrWhiteSpace(account.EndpointId))
            throw new InvalidOperationException("Current Google provider account has no admitted endpoint identity.");
        if (string.IsNullOrWhiteSpace(account.RegionId))
            throw new InvalidOperationException("Current Google provider account has no admitted region identity.");
        if (Capability.IsStale(nowUtc))
            throw new InvalidOperationException("Current Google capability evidence is stale.");

        ProviderAccountReference captured = capability.Account;
        if (!string.Equals(captured.ProviderStableId, account.ProviderStableId, StringComparison.Ordinal) ||
            !string.Equals(captured.AccountId, account.AccountId, StringComparison.Ordinal) ||
            !string.Equals(captured.CredentialReference?.TargetName, account.CredentialReference.TargetName, StringComparison.Ordinal) ||
            !string.Equals(captured.EndpointId, account.EndpointId, StringComparison.Ordinal) ||
            !string.Equals(captured.RegionId, account.RegionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Current Google account identity changed after capability evidence was captured; refresh capability evidence before generation.");
        }

        ProviderCapability synthesis = capability.GetCapability(GoogleGenerationProvider.SynthesizeOperationStableId);
        if (!synthesis.IsUsable)
            throw new InvalidOperationException($"Current Google synthesis capability is not usable: {synthesis.DisabledReason ?? synthesis.State.ToString()}");

        return this;
    }
}

public sealed class GoogleGenerationProductionEvidenceResolver
{
    private readonly IProviderAccountStore _accounts;
    private readonly IProviderCapabilitySnapshotStore _capabilities;
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionEvidenceResolver(
        IProviderAccountStore accounts,
        IProviderCapabilitySnapshotStore capabilities,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GoogleGenerationProductionEvidence> ResolveAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        ProviderAccountSnapshot account = await _accounts.FindAsync(
            GoogleGenerationProvider.StableProviderId,
            accountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No current Google provider account exists for generation.");

        StoredProviderCapabilitySnapshot capability = await _capabilities.GetLatestAsync(
            GoogleGenerationProvider.StableProviderId,
            account.Reference.AccountId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No current Google capability evidence exists for generation.");

        cancellationToken.ThrowIfCancellationRequested();
        return new GoogleGenerationProductionEvidence(account, capability)
            .Validate(_timeProvider.GetUtcNow());
    }
}

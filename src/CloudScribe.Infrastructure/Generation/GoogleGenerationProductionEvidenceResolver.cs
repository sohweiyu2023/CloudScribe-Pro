using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

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

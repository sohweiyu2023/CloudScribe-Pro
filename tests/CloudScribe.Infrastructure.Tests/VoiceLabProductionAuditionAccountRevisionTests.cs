using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProductionAuditionAccountRevisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadAsyncBindsProviderAccountRevisionFromPersistedSnapshot()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountReference reference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference("credential.current"));
        ProviderAccountSnapshot currentAccount = new(
            reference,
            isEnabled: true,
            revision: 2,
            createdAtUtc: Now.AddDays(-1),
            updatedAtUtc: Now.AddMinutes(-1));
        ProviderCapabilitySnapshot snapshot = new(
            reference,
            Now.AddMinutes(-5),
            "voice-lab:test-current",
            []);
        StoredProviderCapabilitySnapshot capability = new(capabilityId, snapshot, Now.AddHours(1));
        VoiceLabCatalogSelection selection = new(
            "voice-1",
            "google",
            "primary",
            "project-1",
            capabilityId.ToString("D"),
            "voice-fingerprint-1",
            CapabilityCurrent: true,
            VoiceEnabled: true,
            AccountProjectAuthorized: true);
        VoiceLabAuditionAuthorizationEvidence callerEvidence = new(
            selection,
            "credential.current",
            "pricing-current",
            "spend-approved",
            PricingCurrent: true,
            SpendApproved: true);
        VoiceLabAuditionRequest request = new(
            selection,
            CachePolicyEligible: false,
            ForceFresh: true,
            ExplicitSpendApproved: true,
            PricingCurrent: true,
            OutputFormat: "wav");
        var loader = new VoiceLabProductionAuditionEvidenceLoader(
            new AccountStore(currentAccount),
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(callerEvidence),
            new FixedTimeProvider(Now));

        VoiceLabAuditionAuthorizationEvidence? resolved = await loader.LoadAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(resolved);
        Assert.Equal(currentAccount.Revision, resolved.AccountRevision);
        Assert.NotEqual(callerEvidence.AccountRevision, resolved.AccountRevision);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AccountStore(ProviderAccountSnapshot account) : IProviderAccountStore
    {
        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference accountReference, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference accountReference, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ProviderAccountSnapshot?>(
                string.Equals(account.Reference.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(account.Reference.AccountId, accountId, StringComparison.Ordinal)
                    ? account
                    : null);
        }

        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccountSnapshot>>([account]);
    }

    private sealed class CapabilityStore(StoredProviderCapabilitySnapshot capability) : IProviderCapabilitySnapshotStore
    {
        public Task<StoredProviderCapabilitySnapshot> SaveAsync(ProviderCapabilitySnapshot snapshot, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<StoredProviderCapabilitySnapshot?>(capability);
        }

        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(string providerStableId, string accountId, int maximumCount = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredProviderCapabilitySnapshot>>([capability]);
    }
}

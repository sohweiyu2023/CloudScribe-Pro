using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabAuditionAccountRevisionBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionLoaderRebindsEvidenceToPersistedAccountRevision()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountReference accountReference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference("credential.current"));
        ProviderAccountSnapshot account = new(
            accountReference,
            isEnabled: true,
            revision: 2,
            createdAtUtc: Now.AddDays(-2),
            updatedAtUtc: Now.AddMinutes(-1));
        StoredProviderCapabilitySnapshot capability = new(
            capabilityId,
            new ProviderCapabilitySnapshot(
                accountReference,
                Now.AddMinutes(-1),
                "voice-lab:revision-binding",
                []),
            Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, accountRevision: 1);
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);
        var loader = new VoiceLabProductionAuditionEvidenceLoader(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence),
            new FixedTimeProvider(Now));

        VoiceLabAuditionAuthorizationEvidence? resolved = await loader.LoadAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(resolved);
        Assert.Equal(2, resolved.AccountRevision);
        Assert.NotSame(evidence, resolved);
    }

    [Fact]
    public async Task AuthorizedExecutorRejectsAccountRevisionDriftBeforeProviderResolution()
    {
        Guid capabilityId = Guid.NewGuid();
        VoiceLabAuditionAuthorizationEvidence approved = CreateEvidence(capabilityId, accountRevision: 1);
        VoiceLabAuditionAuthorizationEvidence current = approved with { AccountRevision = 2 };
        VoiceLabAuditionRequest request = CreateRequest(approved.Selection);
        int providerResolutions = 0;
        var executor = new VoiceLabEvidenceAuthorizedAuditionExecutor(
            approved,
            (_, _) => Task.FromResult(current),
            (_, _, _) =>
            {
                providerResolutions++;
                return ValueTask.FromException<IVoiceLabAuditionProviderAdapter>(
                    new InvalidOperationException("Provider resolution must not occur after revision drift."));
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(request, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("authorization evidence changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, providerResolutions);
    }

    private static VoiceLabAuditionAuthorizationEvidence CreateEvidence(Guid capabilityId, long accountRevision)
    {
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
        return new VoiceLabAuditionAuthorizationEvidence(
            selection,
            "credential.current",
            "pricing-current",
            "spend-approved",
            PricingCurrent: true,
            SpendApproved: true,
            AccountRevision: accountRevision);
    }

    private static VoiceLabAuditionRequest CreateRequest(VoiceLabCatalogSelection selection) => new(
        selection,
        CachePolicyEligible: false,
        ForceFresh: true,
        ExplicitSpendApproved: true,
        PricingCurrent: true,
        OutputFormat: "wav");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AccountStore(ProviderAccountSnapshot account) : IProviderAccountStore
    {
        public Task<ProviderAccountSnapshot> CreateAsync(
            ProviderAccountReference accountReference,
            bool isEnabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderAccountSnapshot> UpdateAsync(
            ProviderAccountReference accountReference,
            bool isEnabled,
            long expectedRevision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderAccountSnapshot?> FindAsync(
            string providerStableId,
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool matches = string.Equals(account.Reference.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(account.Reference.AccountId, accountId, StringComparison.Ordinal);
            return Task.FromResult<ProviderAccountSnapshot?>(matches ? account : null);
        }

        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccountSnapshot>>([account]);
    }

    private sealed class CapabilityStore(StoredProviderCapabilitySnapshot capability) : IProviderCapabilitySnapshotStore
    {
        public Task<StoredProviderCapabilitySnapshot> SaveAsync(
            ProviderCapabilitySnapshot snapshot,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(
            string providerStableId,
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool matches = string.Equals(capability.Snapshot.Account.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(capability.Snapshot.Account.AccountId, accountId, StringComparison.Ordinal);
            return Task.FromResult<StoredProviderCapabilitySnapshot?>(matches ? capability : null);
        }

        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(
            string providerStableId,
            string accountId,
            int maximumCount = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredProviderCapabilitySnapshot>>([capability]);
    }
}

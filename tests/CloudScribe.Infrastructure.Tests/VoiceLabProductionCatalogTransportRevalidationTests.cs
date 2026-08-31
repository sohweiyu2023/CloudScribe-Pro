using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProductionCatalogTransportRevalidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueryAsyncRejectsAuthorizationRevokedDuringProviderQuery()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount();
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId);
        VoiceLabCatalogQuery query = new("google", "primary", "project-1", null, "en-US", false);
        VoiceLabCatalogAuthorizationEvidence approved = CreateEvidence(capabilityId, projectAuthorized: true);
        VoiceLabCatalogAuthorizationEvidence revoked = CreateEvidence(capabilityId, projectAuthorized: false);
        int authorizationLoads = 0;
        int providerCalls = 0;

        var transport = new VoiceLabProductionCatalogTransport(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabCatalogAuthorizationEvidence?>(
                ++authorizationLoads == 1 ? approved : revoked),
            (_, _) =>
            {
                providerCalls++;
                return Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([CreateSelection(capabilityId)]);
            },
            new FixedTimeProvider(Now));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.QueryAsync(
            query,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("project authorization", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, authorizationLoads);
        Assert.Equal(1, providerCalls);
    }

    private static VoiceLabCatalogAuthorizationEvidence CreateEvidence(Guid capabilityId, bool projectAuthorized) => new(
        "google",
        "primary",
        "project-1",
        7,
        "credential.current",
        capabilityId.ToString("D"),
        projectAuthorized,
        PrivateVoiceAccessAuthorized: false);

    private static VoiceLabCatalogSelection CreateSelection(Guid capabilityId) => new(
        "voice-1",
        "google",
        "primary",
        "project-1",
        capabilityId.ToString("D"),
        "voice-fingerprint-1",
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);

    private static ProviderAccountSnapshot CreateAccount()
    {
        ProviderAccountReference reference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference("credential.current"),
            endpointOrigin: new Uri("https://voice.example.test"));
        return new ProviderAccountSnapshot(reference, true, 7, Now.AddDays(-1), Now.AddHours(-1));
    }

    private static StoredProviderCapabilitySnapshot CreateCapability(ProviderAccountReference account, Guid id)
    {
        ProviderCapabilitySnapshot snapshot = new(account, Now.AddMinutes(-5), "voice-lab:test-current", []);
        return new StoredProviderCapabilitySnapshot(id, snapshot, Now.AddHours(1));
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
            return Task.FromResult<ProviderAccountSnapshot?>(account);
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

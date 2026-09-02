using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabCatalogCurrentEvidenceResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveAsyncBindsPersistedAccountAndCapabilityButDoesNotInventProjectAuthorization()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, Now.AddHours(1));
        var resolver = new VoiceLabCatalogCurrentEvidenceResolver(
            new AccountStore(account),
            new CapabilityStore(capability),
            new FixedTimeProvider(Now));

        VoiceLabCatalogAuthorizationEvidence? evidence = await resolver.ResolveAsync(
            CreateQuery(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(evidence);
        Assert.Equal(account.Revision, evidence.AccountRevision);
        Assert.Equal("credential.current", evidence.CredentialReferenceId);
        Assert.Equal(capability.Id.ToString("D"), evidence.CapabilityEvidenceId, ignoreCase: true);
        Assert.False(evidence.ProjectAuthorized);
        Assert.False(evidence.PrivateVoiceAccessAuthorized);
    }

    [Fact]
    public async Task ResolveAsyncFailsClosedWhenCapabilityIsStale()
    {
        ProviderAccountSnapshot account = CreateAccount(isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, Now.AddMinutes(-1));
        var resolver = new VoiceLabCatalogCurrentEvidenceResolver(
            new AccountStore(account),
            new CapabilityStore(capability),
            new FixedTimeProvider(Now));

        VoiceLabCatalogAuthorizationEvidence? evidence = await resolver.ResolveAsync(
            CreateQuery(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(evidence);
    }

    private static VoiceLabCatalogQuery CreateQuery() => new(
        "google", "primary", "project-1", SearchText: null, Locale: "en-US", IncludePrivateVoices: false);

    private static ProviderAccountSnapshot CreateAccount(bool isEnabled)
    {
        ProviderAccountReference reference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference("credential.current"),
            endpointOrigin: new Uri("https://voice.example.test"));
        return new ProviderAccountSnapshot(reference, isEnabled, 7, Now.AddDays(-1), Now.AddMinutes(-5));
    }

    private static StoredProviderCapabilitySnapshot CreateCapability(
        ProviderAccountReference account,
        DateTimeOffset expiresAtUtc)
    {
        ProviderCapabilitySnapshot snapshot = new(account, Now.AddMinutes(-5), "voice-lab:test", []);
        return new StoredProviderCapabilitySnapshot(Guid.NewGuid(), snapshot, expiresAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AccountStore(ProviderAccountSnapshot? account) : IProviderAccountStore
    {
        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference accountReference, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference accountReference, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(account is not null &&
                string.Equals(account.Reference.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(account.Reference.AccountId, accountId, StringComparison.Ordinal)
                ? account
                : null);
        }

        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccountSnapshot>>(account is null ? [] : [account]);
    }

    private sealed class CapabilityStore(StoredProviderCapabilitySnapshot? capability) : IProviderCapabilitySnapshotStore
    {
        public Task<StoredProviderCapabilitySnapshot> SaveAsync(ProviderCapabilitySnapshot snapshot, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(capability is not null &&
                string.Equals(capability.Snapshot.Account.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(capability.Snapshot.Account.AccountId, accountId, StringComparison.Ordinal)
                ? capability
                : null);
        }

        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(string providerStableId, string accountId, int maximumCount = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredProviderCapabilitySnapshot>>(capability is null ? [] : [capability]);
    }
}

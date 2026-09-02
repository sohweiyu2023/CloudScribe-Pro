using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProductionCatalogTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueryAsyncBindsExplicitEndpointCredentialAndCapabilityEvidence()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount(revision: 7, isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabCatalogQuery query = CreateQuery();
        VoiceLabCatalogAuthorizationEvidence evidence = CreateEvidence(capabilityId, account.Revision);
        VoiceLabProviderCatalogRequest? observed = null;
        VoiceLabAdapter adapter = new(request =>
        {
            observed = request;
            return [CreateProviderVoice()];
        });
        var transport = new VoiceLabProductionCatalogTransport(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabCatalogAuthorizationEvidence?>(evidence),
            CreateResolver(adapter),
            new FixedTimeProvider(Now));

        IReadOnlyList<VoiceLabCatalogSelection> results = await transport.QueryAsync(
            query,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Single(results);
        Assert.NotNull(observed);
        Assert.Equal("project-1", observed.ProjectStableId);
        Assert.Equal("credential.current", observed.CredentialReferenceId);
        Assert.Equal(capabilityId.ToString("D"), observed.CapabilityEvidenceId, ignoreCase: true);
        Assert.Equal(new Uri("https://voice.example.test"), observed.EndpointOrigin);
        Assert.Equal("en-US", observed.Locale);
        Assert.False(observed.IncludePrivateVoices);
        Assert.True(adapter.Disposed);
        Assert.Equal("google", results[0].ProviderStableId);
        Assert.Equal("primary", results[0].AccountStableId);
        Assert.Equal("project-1", results[0].ProjectStableId);
    }

    [Fact]
    public async Task QueryAsyncRejectsAccountRevisionDriftBeforeProviderTransport()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount(revision: 8, isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabCatalogAuthorizationEvidence evidence = CreateEvidence(capabilityId, accountRevision: 7);
        int providerCalls = 0;
        VoiceLabProductionCatalogTransport transport = CreateTransport(account, capability, evidence, () => providerCalls++);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.QueryAsync(
            CreateQuery(),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("revision changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public async Task QueryAsyncRejectsCapabilityIdentityDriftBeforeProviderTransport()
    {
        Guid approvedCapabilityId = Guid.NewGuid();
        Guid currentCapabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount(revision: 7, isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, currentCapabilityId, Now.AddHours(1));
        VoiceLabCatalogAuthorizationEvidence evidence = CreateEvidence(approvedCapabilityId, account.Revision);
        int providerCalls = 0;
        VoiceLabProductionCatalogTransport transport = CreateTransport(account, capability, evidence, () => providerCalls++);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.QueryAsync(
            CreateQuery(),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("capability evidence changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public async Task QueryAsyncRejectsDisabledProviderVoice()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount(revision: 7, isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabCatalogAuthorizationEvidence evidence = CreateEvidence(capabilityId, account.Revision);
        VoiceLabAdapter adapter = new(_ => [CreateProviderVoice() with { VoiceEnabled = false }]);
        var transport = new VoiceLabProductionCatalogTransport(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabCatalogAuthorizationEvidence?>(evidence),
            CreateResolver(adapter),
            new FixedTimeProvider(Now));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.QueryAsync(
            CreateQuery(),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("disabled voice", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.Disposed);
    }

    private static VoiceLabProductionCatalogTransport CreateTransport(
        ProviderAccountSnapshot account,
        StoredProviderCapabilitySnapshot capability,
        VoiceLabCatalogAuthorizationEvidence evidence,
        Action onProviderCall)
    {
        VoiceLabAdapter adapter = new(_ =>
        {
            onProviderCall();
            return [CreateProviderVoice()];
        });
        return new VoiceLabProductionCatalogTransport(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabCatalogAuthorizationEvidence?>(evidence),
            CreateResolver(adapter),
            new FixedTimeProvider(Now));
    }

    private static VoiceLabProviderAdapterResolver CreateResolver(IVoiceLabProviderAdapter adapter) =>
        new(new ProviderFactoryRegistry([new FakeFactory(adapter)]));

    private static VoiceLabCatalogQuery CreateQuery() => new(
        "google",
        "primary",
        "project-1",
        SearchText: null,
        Locale: "en-US",
        IncludePrivateVoices: false);

    private static VoiceLabCatalogAuthorizationEvidence CreateEvidence(Guid capabilityId, long accountRevision) => new(
        "google",
        "primary",
        "project-1",
        accountRevision,
        "credential.current",
        capabilityId.ToString("D"),
        ProjectAuthorized: true,
        PrivateVoiceAccessAuthorized: false);

    private static VoiceLabProviderCatalogVoice CreateProviderVoice() => new(
        "voice-1",
        "voice-fingerprint-1",
        VoiceEnabled: true,
        AccountProjectAuthorized: true);

    private static ProviderAccountSnapshot CreateAccount(long revision, bool isEnabled)
    {
        ProviderAccountReference reference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference("credential.current"),
            endpointOrigin: new Uri("https://voice.example.test"));
        return new ProviderAccountSnapshot(reference, isEnabled, revision, Now.AddDays(-1), Now.AddHours(-1));
    }

    private static StoredProviderCapabilitySnapshot CreateCapability(
        ProviderAccountReference account,
        Guid id,
        DateTimeOffset expiresAtUtc)
    {
        ProviderCapabilitySnapshot snapshot = new(
            account,
            Now.AddMinutes(-5),
            "voice-lab:test-current",
            []);
        return new StoredProviderCapabilitySnapshot(id, snapshot, expiresAtUtc);
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

    private sealed class FakeFactory(IProviderAdapter adapter) : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor { get; } = new("google", "google", true, true);

        public ValueTask<IProviderAdapter> CreateAdapterAsync(string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(accountId, "primary", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Voice Lab test account.");
            return ValueTask.FromResult(adapter);
        }
    }

    private sealed class VoiceLabAdapter(Func<VoiceLabProviderCatalogRequest, IReadOnlyList<VoiceLabProviderCatalogVoice>> query) : IVoiceLabProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new("google", "google", true, true);
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<VoiceLabProviderCatalogVoice>> QueryVoiceLabCatalogAsync(
            VoiceLabProviderCatalogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(query(request));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

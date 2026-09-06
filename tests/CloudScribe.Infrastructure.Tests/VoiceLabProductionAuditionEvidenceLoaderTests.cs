using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProductionAuditionEvidenceLoaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadAsyncReturnsEvidenceBoundToCurrentProviderStores()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, "credential.current");
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);
        var loader = new VoiceLabProductionAuditionEvidenceLoader(
            new AccountStore(account),
            new CapabilityStore(capability),
            (resolvedRequest, _) => Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(
                ReferenceEquals(resolvedRequest, request) ? evidence : null),
            new FixedTimeProvider(Now));

        VoiceLabAuditionAuthorizationEvidence? resolved = await loader.LoadAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Same(evidence, resolved);
    }

    [Fact]
    public async Task LoadAsyncRejectsAuthorizationSelectionDrift()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, "credential.current");
        VoiceLabCatalogSelection changedSelection = evidence.Selection with { VoiceStableId = "voice-2" };
        VoiceLabAuditionRequest request = CreateRequest(changedSelection);
        var loader = CreateLoader(account, capability, evidence);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            request,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("selection changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsyncRejectsRequestWithRevokedSpendApprovalBeforeLoadingEvidence()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, "credential.current");
        int evidenceLoads = 0;
        var loader = new VoiceLabProductionAuditionEvidenceLoader(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) =>
            {
                evidenceLoads++;
                return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence);
            },
            new FixedTimeProvider(Now));
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection) with { ExplicitSpendApproved = false };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            request,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("spend approval", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, evidenceLoads);
    }

    [Fact]
    public async Task LoadAsyncRejectsRequestWithStalePricingBeforeLoadingEvidence()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, "credential.current");
        int evidenceLoads = 0;
        var loader = new VoiceLabProductionAuditionEvidenceLoader(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) =>
            {
                evidenceLoads++;
                return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence);
            },
            new FixedTimeProvider(Now));
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection) with { PricingCurrent = false };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            request,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("pricing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, evidenceLoads);
    }

    [Fact]
    public async Task LoadAsyncRejectsCredentialReferenceDrift()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.changed", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, "credential.approved");
        var loader = CreateLoader(account, capability, evidence);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            CreateRequest(evidence.Selection),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("credential reference changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsyncRejectsStaleCapabilityEvidence()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId, Now);
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId, "credential.current");
        var loader = CreateLoader(account, capability, evidence);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            CreateRequest(evidence.Selection),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsyncRejectsCapabilityIdentityDrift()
    {
        Guid approvedCapabilityId = Guid.NewGuid();
        Guid currentCapabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current", isEnabled: true);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, currentCapabilityId, Now.AddHours(1));
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(approvedCapabilityId, "credential.current");
        var loader = CreateLoader(account, capability, evidence);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            CreateRequest(evidence.Selection),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("capability evidence changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VoiceLabProductionAuditionEvidenceLoader CreateLoader(
        ProviderAccountSnapshot account,
        StoredProviderCapabilitySnapshot capability,
        VoiceLabAuditionAuthorizationEvidence evidence) => new(
        new AccountStore(account),
        new CapabilityStore(capability),
        (_, _) => Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence),
        new FixedTimeProvider(Now));

    private static ProviderAccountSnapshot CreateAccount(string credentialReferenceId, bool isEnabled)
    {
        ProviderAccountReference reference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference(credentialReferenceId));
        return new ProviderAccountSnapshot(reference, isEnabled, 1, Now.AddDays(-1), Now.AddHours(-1));
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

    private static VoiceLabAuditionAuthorizationEvidence CreateEvidence(Guid capabilityId, string credentialReferenceId)
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
            credentialReferenceId,
            "pricing-current",
            "spend-approved",
            PricingCurrent: true,
            SpendApproved: true);
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

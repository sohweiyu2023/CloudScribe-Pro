using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProductionAuthorizedAuditionExecutorFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatedExecutorRevalidatesCurrentEvidenceBeforeProviderSubmission()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current");
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId);
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId);
        VoiceLabAuditionAuthorizationEvidence currentEvidence = evidence;
        int evidenceLoads = 0;
        int submitCalls = 0;
        var factory = new VoiceLabProductionAuthorizedAuditionExecutorFactory(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) =>
            {
                evidenceLoads++;
                return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(currentEvidence);
            },
            new FixedTimeProvider(Now),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);

        IVoiceLabAuthorizedAuditionExecutor executor = await factory.CreateAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        currentEvidence = evidence with { SpendAuthorizationId = "spend.changed" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.SubmitAuthorizedAsync(
            request,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(2, evidenceLoads);
        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task CreatedExecutorRejectsPersistedAccountRevisionDriftBeforeProviderSubmission()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current");
        var accountStore = new MutableAccountStore(account);
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId);
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId);
        int submitCalls = 0;
        var factory = new VoiceLabProductionAuthorizedAuditionExecutorFactory(
            accountStore,
            new CapabilityStore(capability),
            (_, _) => Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence),
            new FixedTimeProvider(Now),
            (_, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(Accepted());
            });
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);

        IVoiceLabAuthorizedAuditionExecutor executor = await factory.CreateAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        accountStore.Current = new ProviderAccountSnapshot(
            account.Reference,
            account.IsEnabled,
            account.Revision + 1,
            account.CreatedAtUtc,
            Now);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.SubmitAuthorizedAsync(request, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("authorization evidence changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, submitCalls);
    }

    [Fact]
    public async Task CreatedExecutorSubmitsExactRequestAndFreshEvidenceWhenEvidenceRemainsCurrent()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current");
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId);
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId);
        VoiceLabAuditionRequest? submitted = null;
        VoiceLabAuditionAuthorizationEvidence? submittedEvidence = null;
        int evidenceLoads = 0;
        var factory = new VoiceLabProductionAuthorizedAuditionExecutorFactory(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) =>
            {
                evidenceLoads++;
                return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence);
            },
            new FixedTimeProvider(Now),
            (request, freshEvidence, _) =>
            {
                submitted = request;
                submittedEvidence = freshEvidence;
                return Task.FromResult(Accepted());
            });
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);

        IVoiceLabAuthorizedAuditionExecutor executor = await factory.CreateAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        GenerationProviderResponse response = await executor.SubmitAuthorizedAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Equal(2, evidenceLoads);
        Assert.Same(request, submitted);
        Assert.Equal(evidence with { AccountRevision = account.Revision }, submittedEvidence);
    }

    private static ProviderAccountSnapshot CreateAccount(string credentialReferenceId)
    {
        ProviderAccountReference reference = new(
            "google",
            "primary",
            "Google primary",
            new CredentialReference(credentialReferenceId));
        return new ProviderAccountSnapshot(reference, true, 1, Now.AddDays(-1), Now.AddHours(-1));
    }

    private static StoredProviderCapabilitySnapshot CreateCapability(ProviderAccountReference account, Guid id)
    {
        ProviderCapabilitySnapshot snapshot = new(
            account,
            Now.AddMinutes(-5),
            "voice-lab:test-current",
            []);
        return new StoredProviderCapabilitySnapshot(id, snapshot, Now.AddHours(1));
    }

    private static VoiceLabAuditionAuthorizationEvidence CreateEvidence(Guid capabilityId)
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
            SpendApproved: true);
    }

    private static VoiceLabAuditionRequest CreateRequest(VoiceLabCatalogSelection selection) => new(
        selection,
        CachePolicyEligible: false,
        ForceFresh: true,
        ExplicitSpendApproved: true,
        PricingCurrent: true,
        OutputFormat: "wav");

    private static GenerationProviderResponse Accepted() => new(
        SubmissionDisposition.Accepted,
        "audition-request",
        ReadOnlyMemory<byte>.Empty,
        "audio/wav",
        null,
        "audition-accepted");

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

    private sealed class MutableAccountStore(ProviderAccountSnapshot account) : IProviderAccountStore
    {
        public ProviderAccountSnapshot Current { get; set; } = account;

        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference accountReference, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference accountReference, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderAccountSnapshot current = Current;
            return Task.FromResult<ProviderAccountSnapshot?>(
                string.Equals(current.Reference.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(current.Reference.AccountId, accountId, StringComparison.Ordinal)
                    ? current
                    : null);
        }

        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccountSnapshot>>([Current]);
    }

    private sealed class CapabilityStore(StoredProviderCapabilitySnapshot capability) : IProviderCapabilitySnapshotStore
    {
        public Task<StoredProviderCapabilitySnapshot> SaveAsync(ProviderCapabilitySnapshot snapshot, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<StoredProviderCapabilitySnapshot?>(
                string.Equals(capability.Snapshot.Account.ProviderStableId, providerStableId, StringComparison.Ordinal) &&
                string.Equals(capability.Snapshot.Account.AccountId, accountId, StringComparison.Ordinal)
                    ? capability
                    : null);
        }

        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(string providerStableId, string accountId, int maximumCount = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredProviderCapabilitySnapshot>>([capability]);
    }
}

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
    public async Task CreatedExecutorRejectsEvidenceDriftBeforeResolvingProviderAdapter()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current");
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId);
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId);
        VoiceLabAuditionAuthorizationEvidence currentEvidence = evidence;
        int evidenceLoads = 0;
        var adapter = new AuditionAdapter();
        var providerFactory = new ProviderFactory(adapter);
        var factory = new VoiceLabProductionAuthorizedAuditionExecutorFactory(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) =>
            {
                evidenceLoads++;
                return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(currentEvidence);
            },
            new FixedTimeProvider(Now),
            new ProviderRegistry(providerFactory));
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);

        IVoiceLabAuthorizedAuditionExecutor executor = await factory.CreateAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        currentEvidence = evidence with { SpendAuthorizationId = "spend.changed" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.SubmitAuthorizedAsync(
            request,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(2, evidenceLoads);
        Assert.Equal(0, providerFactory.CreateCalls);
        Assert.Equal(0, adapter.SubmitCalls);
        Assert.Equal(0, adapter.DisposeCalls);
    }

    [Fact]
    public async Task CreatedExecutorSubmitsExactRevalidatedEvidenceThroughAccountBoundAdapter()
    {
        Guid capabilityId = Guid.NewGuid();
        ProviderAccountSnapshot account = CreateAccount("credential.current");
        StoredProviderCapabilitySnapshot capability = CreateCapability(account.Reference, capabilityId);
        VoiceLabAuditionAuthorizationEvidence evidence = CreateEvidence(capabilityId);
        int evidenceLoads = 0;
        var adapter = new AuditionAdapter();
        var providerFactory = new ProviderFactory(adapter);
        var factory = new VoiceLabProductionAuthorizedAuditionExecutorFactory(
            new AccountStore(account),
            new CapabilityStore(capability),
            (_, _) =>
            {
                evidenceLoads++;
                return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence);
            },
            new FixedTimeProvider(Now),
            new ProviderRegistry(providerFactory));
        VoiceLabAuditionRequest request = CreateRequest(evidence.Selection);

        IVoiceLabAuthorizedAuditionExecutor executor = await factory.CreateAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        GenerationProviderResponse response = await executor.SubmitAuthorizedAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Equal(3, evidenceLoads);
        Assert.Equal(1, providerFactory.CreateCalls);
        Assert.Equal("primary", providerFactory.LastAccountId);
        Assert.Equal(1, adapter.SubmitCalls);
        Assert.Equal(1, adapter.DisposeCalls);
        Assert.NotNull(adapter.SubmittedRequest);
        Assert.Equal("google", adapter.SubmittedRequest.ProviderStableId);
        Assert.Equal("primary", adapter.SubmittedRequest.AccountStableId);
        Assert.Equal("project-1", adapter.SubmittedRequest.ProjectStableId);
        Assert.Equal("voice-1", adapter.SubmittedRequest.VoiceStableId);
        Assert.Equal("voice-fingerprint-1", adapter.SubmittedRequest.VoiceFingerprint);
        Assert.Equal(capabilityId.ToString("D"), adapter.SubmittedRequest.CapabilityEvidenceId);
        Assert.Equal("credential.current", adapter.SubmittedRequest.CredentialReferenceId);
        Assert.Equal("pricing-current", adapter.SubmittedRequest.PricingEvidenceId);
        Assert.Equal("spend-approved", adapter.SubmittedRequest.SpendAuthorizationId);
        Assert.Equal(account.Revision, adapter.SubmittedRequest.AccountRevision);
        Assert.Equal("wav", adapter.SubmittedRequest.OutputFormat);
        Assert.True(adapter.SubmittedRequest.ForceFresh);
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

    private sealed class ProviderRegistry(IProviderAdapterFactory factory) : IProviderFactoryRegistry
    {
        public IReadOnlyList<ProviderDescriptor> AvailableProviders => [factory.Descriptor];

        public bool TryGetFactory(string stableProviderId, out IProviderAdapterFactory? resolvedFactory)
        {
            if (string.Equals(stableProviderId, factory.Descriptor.StableId, StringComparison.Ordinal))
            {
                resolvedFactory = factory;
                return true;
            }

            resolvedFactory = null;
            return false;
        }
    }

    private sealed class ProviderFactory(AuditionAdapter adapter) : IProviderAdapterFactory
    {
        public ProviderDescriptor Descriptor { get; } = new("google", "Google", true, true);
        public int CreateCalls { get; private set; }
        public string? LastAccountId { get; private set; }

        public ValueTask<IProviderAdapter> CreateAdapterAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastAccountId = accountId;
            return ValueTask.FromResult<IProviderAdapter>(adapter);
        }
    }

    private sealed class AuditionAdapter : IVoiceLabAuditionProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new("google", "Google", true, true);
        public int SubmitCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public VoiceLabProviderAuditionRequest? SubmittedRequest { get; private set; }

        public Task<GenerationProviderResponse> SubmitVoiceLabAuditionAsync(
            VoiceLabProviderAuditionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCalls++;
            SubmittedRequest = request;
            return Task.FromResult(Accepted());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
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

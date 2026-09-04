using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionCompileAndPrepareServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 5, 0, 0, TimeSpan.Zero);
    private static readonly Uri Endpoint = new("https://texttospeech.googleapis.com/", UriKind.Absolute);

    [Fact]
    public async Task CompileAndPrepareAsyncRejectsDisabledPersistedAccountBeforePublishing()
    {
        ProviderAccountSnapshot persisted = CreatePersistedAccount("credential-current", isEnabled: false);
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        GoogleGenerationProductionCompileAndPrepareService service = CreateService(owner, persisted, "capability-1");
        GoogleGenerationProductionCompileEvidence evidence = CreateEvidence(
            credentialReferenceId: "credential-current",
            capabilityProvenanceId: "capability-1");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompileAndPrepareAsync(evidence, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("account is disabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task CompileAndPrepareAsyncRejectsPersistedAccountBindingDriftBeforePublishing()
    {
        ProviderAccountSnapshot persisted = CreatePersistedAccount("credential-current");
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        GoogleGenerationProductionCompileAndPrepareService service = CreateService(owner, persisted, "capability-1");
        GoogleGenerationProductionCompileEvidence evidence = CreateEvidence(
            credentialReferenceId: "credential-old",
            capabilityProvenanceId: "capability-1");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompileAndPrepareAsync(evidence, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("account evidence changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task CompileAndPrepareAsyncRejectsPersistedCapabilityDriftBeforePublishing()
    {
        ProviderAccountSnapshot persisted = CreatePersistedAccount("credential-current");
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        GoogleGenerationProductionCompileAndPrepareService service = CreateService(owner, persisted, "capability-current");
        GoogleGenerationProductionCompileEvidence evidence = CreateEvidence(
            credentialReferenceId: "credential-current",
            capabilityProvenanceId: "capability-old");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompileAndPrepareAsync(evidence, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("capability evidence changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    private static GoogleGenerationProductionCompileAndPrepareService CreateService(
        GoogleGenerationProductionPendingApprovalStateOwner owner,
        ProviderAccountSnapshot persisted,
        string capabilityProvenanceId)
    {
        StoredProviderCapabilitySnapshot capability = CreatePersistedCapability(
            persisted.Reference,
            capabilityProvenanceId);
        var resolver = new GoogleGenerationProductionEvidenceResolver(
            new AccountStore(persisted),
            new CapabilityStore(capability),
            new FixedTimeProvider(Now));
        return new GoogleGenerationProductionCompileAndPrepareService(
            new GoogleGenerationProductionPendingApprovalPublisher(owner),
            resolver);
    }

    private static GoogleGenerationProductionCompileEvidence CreateEvidence(
        string credentialReferenceId,
        string capabilityProvenanceId)
    {
        var queueState = new GoogleGenerationPersistedQueueState(
            "account-1",
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "idempotency-1",
            false,
            null);
        return new GoogleGenerationProductionCompileEvidence
        {
            Plan = new SpeechPlan("en-US", [new SpeechText("hello")], "speech-plan-1"),
            CompilationOptions = new GoogleSpeechCompilationOptions("en-US", "voice-1", "MP3", 4096),
            Account = new GoogleGenerationAccount("account-1", credentialReferenceId, Endpoint, "global"),
            Capabilities = CreateGoogleCapabilities(capabilityProvenanceId),
            PricingProvenanceId = "pricing-1",
            RequestRevision = 7,
            ProjectId = "project-1",
            ModelId = "model-1",
            IdempotencyKey = "idempotency-1",
            AdmittedTrust = CreateTrustContext(capabilityProvenanceId),
            PreviousState = queueState,
            CurrentState = queueState,
            ResolutionEvidence = GoogleGenerationReconciliationResolutionEvidence.None,
            AccountAuthorized = true,
            ProjectAuthorized = true,
            CapabilityCurrent = true,
            PricingCurrent = true,
            AdmissionCurrent = true,
            AccountCredentialAvailable = true,
            PricingApproved = true,
            PostCompileLimitsSatisfied = true,
            Currency = "USD",
            Scale = 2,
            CurrentEstimateMinorUnits = 125,
            NowUtc = Now,
        };
    }

    private static ProviderAccountSnapshot CreatePersistedAccount(
        string credentialReferenceId,
        bool isEnabled = true)
    {
        var reference = new ProviderAccountReference(
            GoogleGenerationProvider.StableProviderId,
            "account-1",
            "Google primary",
            new CredentialReference(credentialReferenceId),
            "google-tts-v1",
            "global",
            Endpoint);
        return new ProviderAccountSnapshot(reference, isEnabled, 1, Now.AddDays(-1), Now.AddMinutes(-30));
    }

    private static StoredProviderCapabilitySnapshot CreatePersistedCapability(
        ProviderAccountReference account,
        string provenanceId)
    {
        var snapshot = new ProviderCapabilitySnapshot(
            account,
            Now.AddMinutes(-5),
            provenanceId,
            [new ProviderCapability(
                GoogleGenerationProvider.SynthesizeOperationStableId,
                ProviderCapabilityState.Supported,
                ProviderLifecycleState.Available)]);
        return new StoredProviderCapabilitySnapshot(Guid.NewGuid(), snapshot, Now.AddMinutes(30));
    }

    private static GoogleCapabilitySnapshot CreateGoogleCapabilities(string provenanceId) =>
        new(
            "account-1",
            provenanceId,
            Now.AddMinutes(-5),
            Now.AddMinutes(30),
            new HashSet<string>(StringComparer.Ordinal) { "voice-1" },
            new HashSet<string>(StringComparer.Ordinal) { "MP3" },
            4096);

    private static GenerationCacheTrustContext CreateTrustContext(string capabilityProvenanceId) =>
        new(
            GoogleGenerationProvider.StableProviderId,
            "account-1",
            "project-1",
            "endpoint-1",
            "global",
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "model-1",
            "voice-1",
            "voice-fingerprint-1",
            "speech-plan-1",
            "en-US",
            "controls-1",
            "MP3",
            "sample-format-1",
            "adapter-1",
            "compiler-1",
            "ast-1",
            "normalization-1",
            "pricing-1",
            capabilityProvenanceId,
            "governance-1",
            "provider-feature-1",
            "account-capability-1");

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
            bool matches = string.Equals(providerStableId, account.Reference.ProviderStableId, StringComparison.Ordinal)
                && string.Equals(accountId, account.Reference.AccountId, StringComparison.Ordinal);
            return Task.FromResult(matches ? account : null);
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
            bool matches = string.Equals(providerStableId, capability.Snapshot.Account.ProviderStableId, StringComparison.Ordinal)
                && string.Equals(accountId, capability.Snapshot.Account.AccountId, StringComparison.Ordinal);
            return Task.FromResult(matches ? capability : null);
        }

        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(
            string providerStableId,
            string accountId,
            int maximumCount = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredProviderCapabilitySnapshot>>([capability]);
    }
}

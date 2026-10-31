using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleCatalogBootstrapTests
{
    private static readonly Uri CompletionCatalogEndpoint = new("https://texttospeech.googleapis.com/v1/voices");
    private static readonly Uri CompletionSynthesisEndpoint = new("https://texttospeech.googleapis.com/v1/text:synthesize");
    [Fact]
    public async Task UiOwnedCycleIncludesSecondCatalogGetInOneDurableClaim()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        int before = fixture.Handler.Calls;
        Guid refresh = Guid.NewGuid();
        using (var owner = fixture.Service.BeginUiRefresh(refresh))
        {
            await fixture.Service.RefreshConfiguredAsync(refresh, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(refresh, fixture.Runner.Pending);
            var account = (await fixture.Stores.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))[0];
            await fixture.Catalog.LoadAsync(account.Reference, CompletionCatalogEndpoint,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            owner.Succeed();
        }
        Assert.Equal(3, fixture.Handler.Calls - before);
        Assert.Equal(1, fixture.Runner.Refreshes);
        Assert.Null(fixture.Runner.Pending);
        var receipt = Assert.Single(fixture.Runner.Receipts);
        Assert.Equal(true, receipt["catalogVerified"]);
        Assert.Equal(true, receipt["probeInputEmpty"]);
        Assert.Equal(400, receipt["probeHttpStatus"]);
        Assert.Equal("INVALID_ARGUMENT", receipt["probeStatus"]);
    }

    [Fact]
    public async Task DirectBootstrapAlsoOwnsExactlyOneClaimAndVerifiedCompletion()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        await fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, fixture.Runner.Refreshes);
        Assert.Single(fixture.Runner.Receipts);
        Assert.Null(fixture.Runner.Pending);
    }

    [Fact]
    public async Task DirectCatalogAndProbeWithoutOwnerAreRejectedBeforeNetwork()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        int before = fixture.Handler.Calls;
        var account = (await fixture.Stores.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.LoadAsync(account.Reference,
            CompletionCatalogEndpoint, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Probe.VerifyAsync(account.Reference,
            "synthetic-project", CompletionSynthesisEndpoint, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(before, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Runner.Refreshes);
    }

    [Fact]
    public async Task TwentyFifthBootstrapCannotEnterProvider()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        for (int i = 0; i < 24; i++)
            await fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        int before = fixture.Handler.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(before, fixture.Handler.Calls);
        Assert.Equal(24, fixture.Runner.Refreshes);
    }

    [Fact]
    public async Task FailedProbeStopsRunAndCannotRefreshAgain()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        fixture.Handler.RejectProbe = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.True(fixture.Runner.Stopped);
        int before = fixture.Handler.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(before, fixture.Handler.Calls);
    }

    [Fact]
    public async Task PersistenceFailureCannotProduceSuccessfulReceipt()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        fixture.Stores.FailAt = "capability";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.True(fixture.Runner.Stopped);
        Assert.False((bool)Assert.Single(fixture.Runner.Receipts)["catalogVerified"]!);
    }

    [Fact]
    public async Task RestartCannotForgetPendingClaimOrContinueItsEffects()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        fixture.Runner.Pending = Guid.NewGuid(); // persisted runner is intentionally unresolved
        int before = fixture.Handler.Calls;
        var restarted = new CompletionWorkflowGate(() => fixture.Configuration, fixture.Runner);
        var service = fixture.CreateService(restarted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(before, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ExpiredWindowAndConcurrentDifferentRefreshCannotEnterProvider()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        var scope = fixture.Service.BeginUiRefresh(id);
        try
        {
            int before = fixture.Handler.Calls;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
            Assert.Equal(before, fixture.Handler.Calls);
        }
        finally { scope.Dispose(); }
        Assert.True(fixture.Runner.Stopped);
    }

    [Fact]
    public async Task ExpiryAfterClaimButBeforeCredentialResolutionBlocksNetwork()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        fixture.Runner.ExpireAfterClaim = true;
        int before = fixture.Handler.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(before, fixture.Handler.Calls);
        Assert.True(fixture.Runner.Stopped);
    }

    [Fact]
    public async Task FreshCredentialConfigurationIsBlockedBeforeAnyVaultWrite()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        int writes = fixture.Vault.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfigureFreshAsync("other", "Synthetic", "other", "{}".AsMemory(),
            CompletionCatalogEndpoint, CompletionSynthesisEndpoint, "global", TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(writes, fixture.Vault.Writes);
    }

    private sealed class CompletionFixture : IDisposable
    {
        private readonly Clock _clock = new();
        private bool _active;
        public bool ThrowLoad { get; set; }
        private readonly HttpClient _http;
        public Vault Vault { get; } = new();
        public Stores Stores { get; }
        public ProviderHandler Handler { get; } = new();
        public CompletionRunner Runner { get; } = new();
        public ResumeEvidenceStore Resume { get; } = new();
        public CompletionGateConfiguration Configuration { get; } = new(1, "synthetic", "", "synthetic", "", "", "synthetic", "",
            "synthetic", "synthetic", "", "", "", "synthetic-account", "synthetic-project", "synthetic-credential",
            "https://texttospeech.googleapis.com/v1/voices", "https://texttospeech.googleapis.com/v1/text:synthesize", "synthetic", "", "en-US-Standard-C", "en-US")
        { Pin = new string('a', 64), ConfigPath = "synthetic" };
        public CompletionWorkflowGate Gate { get; }
        public GoogleVoiceCatalogClient Catalog { get; }
        public GoogleSynthesisCapabilityProbe Probe { get; }
        public GoogleTextToSpeechCatalogBootstrapService Service { get; }

        public CompletionFixture()
        {
            Stores = new(_clock); _http = new(Handler);
            Gate = new(() => ThrowLoad ? throw new IOException("synthetic missing gate material") : _active ? Configuration : null, Runner);
            Catalog = new(_http, new Resolver(), completionGate: Gate);
            Probe = new(_http, new Resolver(), _clock, completionGate: Gate);
            Service = CreateService(Gate);
        }

        public GoogleTextToSpeechCatalogBootstrapService CreateService(CompletionWorkflowGate gate) => new(new(Vault, gate),
            new(_http, new Resolver(), completionGate: gate), new(_http, new Resolver(), _clock, completionGate: gate),
            Stores, Stores, Stores, Stores, _clock, completionGate: gate, resumeAdmission: Resume);

        public async Task InitializeAsync()
        {
            string json = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = "synthetic-project",
                client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
                private_key_id = "synthetic-key",
                private_key = "-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----",
                token_uri = "https://oauth2.googleapis.com/token"
            });
            await Service.ConfigureFreshAsync("synthetic-account", "Synthetic", "synthetic-credential", json.AsMemory(),
                CompletionCatalogEndpoint, CompletionSynthesisEndpoint, "global", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _active = true;
        }

        public void Dispose() => _http.Dispose();
    }

    [Fact]
    public async Task OwnerCannotCompleteWhileSecondCatalogResponseIsPending()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        var owner = fixture.Service.BeginUiRefresh(id);
        await fixture.Service.RefreshConfiguredAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.Handler.CatalogGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.CatalogPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.PauseAtCall = fixture.Handler.Calls + 1;
        var account = (await fixture.Stores.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))[0];
        var pending = fixture.Catalog.LoadAsync(account.Reference, CompletionCatalogEndpoint, TestContext.Current.CancellationToken);
        await fixture.Handler.CatalogPaused.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        owner.Succeed();
        Assert.Throws<CompletionGateException>(owner.Dispose);
        Assert.True(fixture.Runner.Stopped);
        fixture.Handler.CatalogGate.TrySetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending).ConfigureAwait(true);
        Assert.False((bool)Assert.Single(fixture.Runner.Receipts)["catalogVerified"]!);
    }

    [Fact]
    public async Task InheritedAsyncLeaseCannotBeUsedAfterOwnerCloses()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        var owner = fixture.Service.BeginUiRefresh(id);
        await fixture.Service.RefreshConfiguredAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var resumeChild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var account = (await fixture.Stores.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))[0];
        var child = Task.Run(async () =>
        {
            await resumeChild.Task.ConfigureAwait(false);
            await fixture.Catalog.LoadAsync(account.Reference, CompletionCatalogEndpoint, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }, TestContext.Current.CancellationToken);
        owner.Succeed(); owner.Dispose();
        int before = fixture.Handler.Calls;
        resumeChild.TrySetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => child).ConfigureAwait(true);
        Assert.Equal(before, fixture.Handler.Calls);
    }

    [Fact]
    public async Task InconsistentSecondCatalogStopsWithoutSuccessfulReceipt()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        using (fixture.Service.BeginUiRefresh(id))
        {
            await fixture.Service.RefreshConfiguredAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
            fixture.Handler.DifferentCatalog = true;
            var account = (await fixture.Stores.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))[0];
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.LoadAsync(account.Reference,
                CompletionCatalogEndpoint, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }
        Assert.True(fixture.Runner.Stopped);
    }

    private sealed class CompletionRunner : ICompletionGateCommandRunner
    {
        public Guid? Pending { get; set; }
        public int Refreshes { get; private set; }
        public bool Stopped { get; private set; }
        public bool ExpireAfterClaim { get; set; }
        public List<IReadOnlyDictionary<string, object?>> Receipts { get; } = [];
        public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt)
        {
            if (string.Equals(command, "complete", StringComparison.Ordinal))
            {
                Receipts.Add(receipt!);
                Stopped |= !Equals(receipt!["outcome"], "Succeeded") || ExpireAfterClaim;
                Pending = null;
                if (Stopped) throw new InvalidOperationException("Synthetic run stopped.");
                return;
            }
            if (Stopped || (ExpireAfterClaim && Pending is not null)) throw new InvalidOperationException("Synthetic expired/stopped gate.");
            if (string.Equals(command, "claim-refresh", StringComparison.Ordinal))
            {
                if (Pending is not null || Refreshes >= 24) throw new InvalidOperationException("Synthetic pending/limit.");
                Pending = id; Refreshes++;
            }
            else if (Pending != id) throw new InvalidOperationException("Synthetic claim mismatch.");
        }
    }

    private sealed class ResumeEvidenceStore : IChirpReservationResumeAdmissionStore
    {
        public ChirpReservationResumePolicy? Policy { get; set; }
        public Task<ChirpReservationResumePolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Policy);
    }

    [Fact]
    public async Task ReopenedUiRetainsExactAdmittedSnapshotWithoutExtendingItsExpiry()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        var prior = fixture.Stores.Capability!;
        fixture.Resume.Policy = JsonSerializer.Deserialize<ChirpReservationResumePolicy>(JsonSerializer.Serialize(new
        {
            CatalogSnapshotId = prior.Id,
            CatalogExpiresAtUtc = prior.ExpiresAtUtc,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            ExecutionEnvelope = new { CapabilityProvenanceId = prior.Snapshot.ProvenanceId }
        }));
        var restartedGate = new CompletionWorkflowGate(() => fixture.Configuration, fixture.Runner);
        var restartedService = fixture.CreateService(restartedGate);
        var result = await restartedService.RefreshConfiguredAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Same(prior, fixture.Stores.Capability);
        Assert.Equal(prior.Id, result.CapabilityEvidence.Id);
        Assert.Equal(prior.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Equal(1, fixture.Runner.Refreshes);
    }

    [Fact]
    public async Task ExpiredResumeDoesNotRenewOldCatalogIdentity()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        var prior = fixture.Stores.Capability!;
        fixture.Resume.Policy = JsonSerializer.Deserialize<ChirpReservationResumePolicy>(JsonSerializer.Serialize(new
        { CatalogSnapshotId = prior.Id, CatalogExpiresAtUtc = prior.ExpiresAtUtc, ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        var result = await fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotEqual(prior.Id, result.CapabilityEvidence.Id);
        Assert.Null(fixture.Runner.Pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedObservedCatalogOrProbeCannotRetainAdmittedIdentity(bool catalog)
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        var prior = fixture.Stores.Capability!;
        fixture.Resume.Policy = JsonSerializer.Deserialize<ChirpReservationResumePolicy>(JsonSerializer.Serialize(new
        {
            CatalogSnapshotId = prior.Id,
            CatalogExpiresAtUtc = prior.ExpiresAtUtc,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            ExecutionEnvelope = new { CapabilityProvenanceId = prior.Snapshot.ProvenanceId }
        }));
        fixture.Handler.DifferentCatalogMetadata = catalog;
        fixture.Handler.DifferentProbeBody = !catalog;
        var result = await fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotEqual(prior.Id, result.CapabilityEvidence.Id);
        Assert.NotEqual(prior.Snapshot.ProvenanceId, result.CapabilityEvidence.Snapshot.ProvenanceId, StringComparer.Ordinal);
    }

    [Fact]
    public async Task UnexpectedAudioInEmptyProbeCannotEstablishRefreshEvidence()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        var prior = fixture.Stores.Capability;
        fixture.Handler.UnexpectedProbeAudio = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Same(prior, fixture.Stores.Capability);
        Assert.True(fixture.Runner.Stopped);
    }

    [Fact]
    public async Task GateCleanupFailurePreservesTheActualProbeFailure()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        fixture.Handler.RejectProbe = true;
        fixture.Handler.BeforeProbeResponse = () => fixture.ThrowLoad = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
        Assert.NotNull(fixture.Runner.Pending);
        Assert.Empty(fixture.Runner.Receipts);
    }
}

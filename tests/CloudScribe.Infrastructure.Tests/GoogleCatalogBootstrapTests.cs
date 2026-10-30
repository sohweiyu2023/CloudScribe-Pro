using System.Net;
using System.Text.Json;
using CloudScribe.Application.Providers;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleCatalogBootstrapTests
{
    [Fact]
    public async Task FreshAndRefreshedEvidencePassProductionValidationWithoutReplacingAccountOrKey()
    {
        var clock = new Clock();
        var stores = new Stores(clock);
        var vault = new Vault();
        var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);
        using var handler = new ProviderHandler();
        using var client = new HttpClient(handler);
        var resolver = new Resolver();
        var service = new GoogleTextToSpeechCatalogBootstrapService(onboarding,
            new GoogleVoiceCatalogClient(client, resolver), new GoogleSynthesisCapabilityProbe(client, resolver, clock),
            stores, stores, stores, stores, clock);
        var cancellation = TestContext.Current.CancellationToken;
        string json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "synthetic-project",
            client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
            private_key_id = "synthetic-key",
            private_key = "-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----",
            token_uri = "https://oauth2.googleapis.com/token",
        });
        var first = await service.ConfigureFreshAsync("synthetic-account", "Synthetic", "synthetic-credential", json.AsMemory(),
            new Uri("https://texttospeech.googleapis.com/v1/voices"), new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "global", cancellation).ConfigureAwait(true);
        new GoogleGenerationProductionEvidence(first.Account, first.CapabilityEvidence).Validate(clock.GetUtcNow());
        Assert.True(first.CapabilityEvidence.Snapshot.CapturedAtUtc >= first.Account.UpdatedAtUtc);
        Assert.Equal(3, vault.Writes);
        clock.Now = clock.Now.AddMinutes(31);
        Assert.True(first.CapabilityEvidence.IsStale(clock.GetUtcNow()));

        var refreshed = await service.RefreshConfiguredAsync(cancellation).ConfigureAwait(true);

        new GoogleGenerationProductionEvidence(refreshed.Account, refreshed.CapabilityEvidence).Validate(clock.GetUtcNow());
        Assert.Same(first.Account, refreshed.Account);
        Assert.Equal(1, stores.Creates);
        Assert.Equal(3, vault.Writes);
        Assert.Equal(4, handler.Calls);
        Assert.True(stores.Authorization!.IsCurrent(clock.GetUtcNow()));
        Assert.Equal(refreshed.CapabilityEvidence.Snapshot.ProvenanceId, stores.Authorization.CapabilityProvenanceId);

        handler.RejectProbe = true;
        StoredProviderCapabilitySnapshot admitted = stores.Capability!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshConfiguredAsync(cancellation)).ConfigureAwait(true);
        Assert.Same(admitted, stores.Capability);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool RejectProbe { get; set; }
        public bool MalformedCatalog { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method == HttpMethod.Get && MalformedCatalog)
                return new(HttpStatusCode.OK) { Content = new StringContent("{private-response-body") };
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"voices\":[{\"name\":\"en-US-Standard-C\",\"languageCodes\":[\"en-US\"],\"ssmlGender\":\"FEMALE\",\"naturalSampleRateHertz\":24000}]}") };
            Assert.Equal("{}", await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new(RejectProbe ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}") };
        }
    }

    private sealed class Resolver : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) => ValueTask.FromResult("synthetic-token");
    }

    private sealed class Vault : ICredentialVault
    {
        private readonly Dictionary<string, char[]> _items = new(StringComparer.Ordinal);
        public int Writes { get; private set; }
        public Exception? ReadFailure { get; set; }
        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        { Writes++; _items[reference.TargetName] = secret.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            if (ReadFailure is { } failure) throw failure;
            return ValueTask.FromResult(_items.TryGetValue(reference.TargetName, out var value) ? new CredentialSecret(value.ToArray()) : null);
        }
        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) => ValueTask.FromResult(_items.Remove(reference.TargetName));
    }

    private sealed class Stores(Clock clock) : IProviderAccountStore, IProviderCapabilitySnapshotStore, IVoiceLabProjectAuthorizationStore, IGoogleGenerationProjectAuthorizationStore
    {
        private ProviderAccountSnapshot? _account;
        public StoredProviderCapabilitySnapshot? Capability { get; private set; }
        public GoogleGenerationProjectAuthorizationEvidence? Authorization { get; private set; }
        public int Creates { get; private set; }
        public string? FailAt { get; set; }
        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference account, bool isEnabled, CancellationToken cancellationToken = default)
        { Creates++; clock.Now = DateTimeOffset.UtcNow.AddSeconds(1); _account = new(account, isEnabled, 1, clock.Now, clock.Now); return Task.FromResult(_account); }
        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference account, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default) => Task.FromResult(_account);
        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderAccountSnapshot>>(_account is null ? [] : [_account]);
        public Task<StoredProviderCapabilitySnapshot> SaveAsync(ProviderCapabilitySnapshot snapshot, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        { if (string.Equals(FailAt, "capability", StringComparison.Ordinal)) throw new InvalidOperationException("private-storage-detail"); Capability = new(Guid.NewGuid(), snapshot, expiresAtUtc); return Task.FromResult(Capability); }
        public Task<StoredProviderCapabilitySnapshot?> GetLatestAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default) => Task.FromResult(Capability);
        public Task<IReadOnlyList<StoredProviderCapabilitySnapshot>> ListRecentAsync(string providerStableId, string accountId, int maximumCount = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        Task<VoiceLabProjectAuthorizationEvidence?> IVoiceLabProjectAuthorizationStore.LoadCurrentAsync(string providerId, string accountId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveVerifiedAsync(VoiceLabProjectAuthorizationEvidence evidence, CancellationToken cancellationToken = default) => string.Equals(FailAt, "project", StringComparison.Ordinal) ? Task.FromException(new InvalidOperationException("private-storage-detail")) : Task.CompletedTask;
        public Task<GoogleGenerationProjectAuthorizationEvidence?> LoadCurrentAsync(string accountId, string projectId, string modelId, CancellationToken cancellationToken = default) => Task.FromResult(Authorization);
        public Task<GoogleGenerationProjectAuthorizationEvidence?> LoadSingleCurrentForProjectAsync(string accountId, string projectId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(Authorization);
        public Task SaveVerifiedAsync(GoogleGenerationProjectAuthorizationEvidence evidence, CancellationToken cancellationToken = default)
        { if (string.Equals(FailAt, "voices", StringComparison.Ordinal)) throw new InvalidOperationException("private-storage-detail"); Authorization = evidence; return Task.CompletedTask; }
    }
}

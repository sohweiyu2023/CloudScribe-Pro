using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleServiceAccountCredentialOnboardingTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("credential")]
    [InlineData("project")]
    [InlineData("endpoint")]
    public async Task LongAudioLocalReadinessChecksConfiguredIdentityWithoutTokenOrNetwork(string fault)
    {
        var vault = new MemoryVault(); var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);
        var token = TestContext.Current.CancellationToken;
        await onboarding.ImportAsync("synthetic-account", CreateJson("-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----"), CatalogEndpoint, SynthesisEndpoint, token);
        var reference = new ProviderAccountReference(GoogleGenerationProvider.StableProviderId, "synthetic-account", "Synthetic account",
            new CredentialReference(fault is "credential" ? "other-reference" : "synthetic-account"),
            endpointOrigin: fault is "endpoint" ? new Uri("https://other.googleapis.com") : new Uri("https://texttospeech.googleapis.com"));
        var snapshot = new ProviderAccountSnapshot(reference, fault is not "disabled", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var accounts = new LongAudioAccounts(fault is "missing" ? null : snapshot);
        Guid id = Guid.NewGuid();
        var request = GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-account",
            "gs://synthetic-private/" + id.ToString("N") + ".mp3", "Synthetic text.", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3");
        var readiness = new GoogleLongAudioAccountReadiness(accounts, onboarding);
        string project = fault is "project" ? "different-project" : "synthetic-project";
        if (fault is "valid") await readiness.RequireCurrentAsync(request, "synthetic-account", project, token);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.RequireCurrentAsync(request, "synthetic-account", project, token).AsTask());
        Assert.Equal(3, vault.Items.Count); // Readiness imports, deletes and refreshes nothing.
    }

    private sealed class LongAudioAccounts(ProviderAccountSnapshot? snapshot) : IProviderAccountStore
    {
        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(GoogleGenerationProvider.StableProviderId, providerStableId); Assert.Equal("synthetic-account", accountId);
            return Task.FromResult(snapshot);
        }
        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference account, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference account, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

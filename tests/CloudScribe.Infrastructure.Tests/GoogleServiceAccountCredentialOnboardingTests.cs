using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleServiceAccountCredentialOnboardingTests
{
    private static readonly Uri CatalogEndpoint = new("https://texttospeech.googleapis.com/v1/voices");
    private static readonly Uri SynthesisEndpoint = new("https://texttospeech.googleapis.com/v1/text:synthesize");

    [Fact]
    public async Task ImportResolvesEndpointsAndRemovesAllValidatedVaultEntries()
    {
        var vault = new MemoryVault();
        var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);
        var token = TestContext.Current.CancellationToken;

        var identity = await onboarding.ImportAsync("synthetic-account", CreateJson("-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----"), CatalogEndpoint, SynthesisEndpoint, token).ConfigureAwait(true);

        Assert.Equal("synthetic-project", identity.ProjectId);
        Assert.Equal(3, vault.Items.Count);
        Assert.All(vault.Items.Keys, name => Assert.Equal(name, new CredentialReference(name).TargetName));
        Assert.Equal(CatalogEndpoint, await onboarding.ResolveVoiceCatalogEndpointAsync("synthetic-account", token).ConfigureAwait(true));
        Assert.Equal(SynthesisEndpoint, await onboarding.ResolveSynthesisEndpointAsync("synthetic-account", token).ConfigureAwait(true));
        Assert.True(await onboarding.RemoveAsync("synthetic-account", token).ConfigureAwait(true));
        Assert.Empty(vault.Items);
    }

    [Fact]
    public async Task ImportedSyntheticKeyResolvesThroughExistingOAuthPathAndCachesToken()
    {
        using var rsa = RSA.Create(2048);
        var vault = new MemoryVault();
        var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);
        var token = TestContext.Current.CancellationToken;
        await onboarding.ImportAsync("synthetic-account", CreateJson(rsa.ExportPkcs8PrivateKeyPem()), CatalogEndpoint, SynthesisEndpoint, token).ConfigureAwait(true);
        using var handler = new SyntheticOAuthHandler(rsa);
        using var client = new HttpClient(handler);
        using var resolver = new VaultBackedTransientCredentialResolver(vault, client);

        Assert.Equal("synthetic-access-token", await resolver.ResolveAccessTokenAsync("synthetic-account", token).ConfigureAwait(true));
        Assert.Equal("synthetic-access-token", await resolver.ResolveAccessTokenAsync("synthetic-account", token).ConfigureAwait(true));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FailedMetadataWriteRollsBackPreviouslyStoredKey()
    {
        var vault = new MemoryVault { FailOnWrite = 2 };
        var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);

        await Assert.ThrowsAsync<IOException>(() => onboarding.ImportAsync("synthetic-account", CreateJson("-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----"), CatalogEndpoint, SynthesisEndpoint, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Empty(vault.Items);
    }

    [Fact]
    public async Task InvalidCallerReferenceIsRejectedBeforeAnyVaultWrite()
    {
        var vault = new MemoryVault();
        var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);

        await Assert.ThrowsAsync<ArgumentException>(() => onboarding.ImportAsync("invalid/reference", CreateJson("-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----"), CatalogEndpoint, SynthesisEndpoint, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Empty(vault.Items);
    }

    private static ReadOnlyMemory<char> CreateJson(string privateKey) => JsonSerializer.Serialize(new
    {
        type = "service_account",
        project_id = "synthetic-project",
        client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
        private_key_id = "synthetic-key",
        private_key = privateKey,
        token_uri = "https://oauth2.googleapis.com/token",
    }).AsMemory();

    private sealed class MemoryVault : ICredentialVault
    {
        public Dictionary<string, char[]> Items { get; } = new(StringComparer.Ordinal);
        public int FailOnWrite { get; init; }
        private int _writes;

        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_writes == FailOnWrite)
                throw new IOException("Synthetic vault write failure.");
            Items[reference.TargetName] = secret.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Items.TryGetValue(reference.TargetName, out char[]? value) ? new CredentialSecret(value.ToArray()) : null);
        }

        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Items.Remove(reference.TargetName));
        }
    }

    private sealed class SyntheticOAuthHandler(RSA rsa) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(new Uri("https://oauth2.googleapis.com/token"), request.RequestUri);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var fields = body.Split('&').Select(item => item.Split('=', 2)).ToDictionary(item => item[0], item => Uri.UnescapeDataString(item[1]), StringComparer.Ordinal);
            Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", fields["grant_type"]);
            string[] parts = fields["assertion"].Split('.');
            byte[] signature = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/').PadRight((parts[2].Length + 3) / 4 * 4, '='));
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"synthetic-access-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}

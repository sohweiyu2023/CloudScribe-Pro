using CloudScribe.Application.Generation;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage5PrivateCacheKeyProviderTests
{
    [Fact]
    public async Task VaultBackedKeySurvivesProviderRecreation()
    {
        var vault = new MemoryCredentialVault();
        byte[] first;
        await using (var scope = new AsyncKeyScope(await new VaultBackedGenerationPrivateCacheKeyProvider(vault).GetOrCreateAsync()))
        {
            first = scope.Material.Span.ToArray();
        }

        byte[] second;
        await using (var scope = new AsyncKeyScope(await new VaultBackedGenerationPrivateCacheKeyProvider(vault).GetOrCreateAsync()))
        {
            second = scope.Material.Span.ToArray();
        }

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.Single(vault.Items);
    }

    [Fact]
    public async Task InvalidVaultMaterialFailsClosedInsteadOfRotatingSilently()
    {
        var vault = new MemoryCredentialVault();
        await vault.StoreAsync(new CredentialReference("cache-private-hmac-v2-23"), "not-valid-base64".AsMemory());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var material = await new VaultBackedGenerationPrivateCacheKeyProvider(vault).GetOrCreateAsync();
        });
    }

    private sealed class MemoryCredentialVault : ICredentialVault
    {
        public Dictionary<string, char[]> Items { get; } = new(StringComparer.Ordinal);

        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items[reference.TargetName] = secret.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<CredentialSecret?>(
                Items.TryGetValue(reference.TargetName, out var value) ? new CredentialSecret(value.ToArray()) : null);
        }

        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Items.Remove(reference.TargetName));
        }
    }

    private sealed class AsyncKeyScope(GenerationPrivateCacheKeyMaterial material) : IAsyncDisposable
    {
        public GenerationPrivateCacheKeyMaterial Material { get; } = material;

        public ValueTask DisposeAsync()
        {
            Material.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

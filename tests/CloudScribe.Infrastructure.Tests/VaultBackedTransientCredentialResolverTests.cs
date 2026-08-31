using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VaultBackedTransientCredentialResolverTests
{
    [Fact]
    public async Task ResolveAccessTokenAsyncReadsOnlyExplicitReference()
    {
        var vault = new RecordingCredentialVault("token-value");
        var resolver = new VaultBackedTransientCredentialResolver(vault);

        string token = await resolver.ResolveAccessTokenAsync(
            "google-account-token",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("token-value", token);
        Assert.Equal("google-account-token", vault.LastReadReference?.TargetName);
        Assert.Equal(1, vault.ReadCount);
    }

    [Fact]
    public async Task ResolveAccessTokenAsyncFailsClosedWhenReferenceIsMissing()
    {
        var resolver = new VaultBackedTransientCredentialResolver(new RecordingCredentialVault(null));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAccessTokenAsync(
                "missing-token",
                TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Contains("unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAccessTokenAsyncFailsClosedForWhitespaceCredential()
    {
        var resolver = new VaultBackedTransientCredentialResolver(new RecordingCredentialVault("   \t"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAccessTokenAsync(
                "blank-token",
                TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Contains("no bearer access token", error.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingCredentialVault(string? secret) : ICredentialVault
    {
        public CredentialReference? LastReadReference { get; private set; }
        public int ReadCount { get; private set; }

        public ValueTask StoreAsync(
            CredentialReference reference,
            ReadOnlyMemory<char> secretValue,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CredentialSecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReadReference = reference;
            ReadCount++;
            CredentialSecret? result = secret is null ? null : new CredentialSecret(secret.ToCharArray());
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

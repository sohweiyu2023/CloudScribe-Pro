using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Safety;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class RestoreRecoveryExecutionCompositionFactoryTests
{
    [Fact]
    public async Task CreateAsyncRejectsMissingVaultAuthenticationKey()
    {
        var reference = new CredentialReference("CloudScribe/RestoreRecovery/TestAuthenticationKey");
        var vault = new TestCredentialVault(secret: null);
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        string journalPath = CreateUnusedJournalPath();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                Path.GetTempPath(),
                Path.GetTempPath(),
                new AtomicVerifiedRestoreExecutor(),
                cancellationToken));

        Assert.Equal(
            "Restore recovery journal authentication key is not available in the credential vault.",
            exception.Message);
        Assert.Equal(1, vault.ReadCount);
        Assert.Equal(reference, vault.LastReadReference);
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public async Task CreateAsyncRejectsAuthenticationKeyBelowMinimumStrength()
    {
        var reference = new CredentialReference("CloudScribe/RestoreRecovery/TestAuthenticationKey");
        var vault = new TestCredentialVault("short-key".ToCharArray());
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        string journalPath = CreateUnusedJournalPath();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                Path.GetTempPath(),
                Path.GetTempPath(),
                new AtomicVerifiedRestoreExecutor(),
                cancellationToken));

        Assert.Equal(
            "Restore recovery journal authentication key must contain at least 256 bits of UTF-8 key material.",
            exception.Message);
        Assert.Equal(1, vault.ReadCount);
        Assert.Equal(reference, vault.LastReadReference);
        Assert.False(File.Exists(journalPath));
    }

    private static string CreateUnusedJournalPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "CloudScribe.Tests",
            Guid.NewGuid().ToString("N"),
            "restore-recovery.journal");

    private sealed class TestCredentialVault : ICredentialVault
    {
        private readonly char[]? _secret;

        public TestCredentialVault(char[]? secret)
        {
            _secret = secret;
        }

        public int ReadCount { get; private set; }

        public CredentialReference? LastReadReference { get; private set; }

        public ValueTask StoreAsync(
            CredentialReference reference,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<CredentialSecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastReadReference = reference;
            return ValueTask.FromResult(
                _secret is null
                    ? null
                    : new CredentialSecret((char[])_secret.Clone()));
        }

        public ValueTask<bool> DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }
}

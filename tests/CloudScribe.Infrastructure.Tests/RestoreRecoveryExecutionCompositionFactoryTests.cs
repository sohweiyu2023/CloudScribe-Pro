using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Safety;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class RestoreRecoveryExecutionCompositionFactoryTests
{
    private const string AuthenticationKeyTarget = "cloudscribe.restore-recovery.test-authentication-key";

    [Fact]
    public async Task CreateAsyncRejectsMissingVaultAuthenticationKey()
    {
        var reference = new CredentialReference(AuthenticationKeyTarget);
        var vault = new TestCredentialVault(secret: null);
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        (string journalPath, string stagingRoot, string backupRoot) = CreateRecoveryPaths();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                stagingRoot,
                backupRoot,
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
        var reference = new CredentialReference(AuthenticationKeyTarget);
        var vault = new TestCredentialVault("short-key".ToCharArray());
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        (string journalPath, string stagingRoot, string backupRoot) = CreateRecoveryPaths();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                stagingRoot,
                backupRoot,
                new AtomicVerifiedRestoreExecutor(),
                cancellationToken));

        Assert.Equal(
            "Restore recovery journal authentication key must contain at least 256 bits of UTF-8 key material.",
            exception.Message);
        Assert.Equal(1, vault.ReadCount);
        Assert.Equal(reference, vault.LastReadReference);
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public async Task CreateAsyncHonorsCancellationBeforeCredentialAccess()
    {
        var reference = new CredentialReference(AuthenticationKeyTarget);
        var vault = new TestCredentialVault("0123456789abcdef0123456789abcdef".ToCharArray());
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        (string journalPath, string stagingRoot, string backupRoot) = CreateRecoveryPaths();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                stagingRoot,
                backupRoot,
                new AtomicVerifiedRestoreExecutor(),
                cancellation.Token));

        Assert.Equal(0, vault.ReadCount);
        Assert.Null(vault.LastReadReference);
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public async Task CreateAsyncRejectsRelativeRecoveryPathBeforeCredentialAccess()
    {
        var reference = new CredentialReference(AuthenticationKeyTarget);
        var vault = new TestCredentialVault("0123456789abcdef0123456789abcdef".ToCharArray());
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        (_, string stagingRoot, string backupRoot) = CreateRecoveryPaths();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                "restore-recovery.journal",
                stagingRoot,
                backupRoot,
                new AtomicVerifiedRestoreExecutor(),
                TestContext.Current.CancellationToken));

        Assert.Equal("Restore recovery paths must be explicitly fully qualified.", exception.Message);
        Assert.Equal(0, vault.ReadCount);
    }

    [Fact]
    public async Task CreateAsyncRejectsOverlappingStagingAndBackupRootsBeforeCredentialAccess()
    {
        var reference = new CredentialReference(AuthenticationKeyTarget);
        var vault = new TestCredentialVault("0123456789abcdef0123456789abcdef".ToCharArray());
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        (string journalPath, string stagingRoot, _) = CreateRecoveryPaths();
        string nestedBackupRoot = Path.Combine(stagingRoot, "backup");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                stagingRoot,
                nestedBackupRoot,
                new AtomicVerifiedRestoreExecutor(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Restore recovery journal, staging, and backup paths must not overlap.",
            exception.Message);
        Assert.Equal(0, vault.ReadCount);
    }

    [Fact]
    public async Task CreateAsyncRejectsJournalInsideBackupRootBeforeCredentialAccess()
    {
        var reference = new CredentialReference(AuthenticationKeyTarget);
        var vault = new TestCredentialVault("0123456789abcdef0123456789abcdef".ToCharArray());
        var factory = new RestoreRecoveryExecutionCompositionFactory(vault, TimeProvider.System);
        (_, string stagingRoot, string backupRoot) = CreateRecoveryPaths();
        string journalPath = Path.Combine(backupRoot, "journal.json");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(
                reference,
                journalPath,
                stagingRoot,
                backupRoot,
                new AtomicVerifiedRestoreExecutor(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Restore recovery journal, staging, and backup paths must not overlap.",
            exception.Message);
        Assert.Equal(0, vault.ReadCount);
    }

    private static (string JournalPath, string StagingRoot, string BackupRoot) CreateRecoveryPaths()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "CloudScribe.Tests",
            Guid.NewGuid().ToString("N"));
        return (
            Path.Combine(root, "journal", "restore-recovery.journal"),
            Path.Combine(root, "staging"),
            Path.Combine(root, "backup"));
    }

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

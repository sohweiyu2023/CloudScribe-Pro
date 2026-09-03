using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreRecoveryExecutionCompositionFactory
{
    private readonly ICredentialVault _credentialVault;
    private readonly TimeProvider _timeProvider;

    public RestoreRecoveryExecutionCompositionFactory(
        ICredentialVault credentialVault,
        TimeProvider timeProvider)
    {
        _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<RestoreRecoveryExecutionComposition> CreateAsync(
        CredentialReference authenticationKeyReference,
        string journalPath,
        string stagingRoot,
        string backupRoot,
        AtomicVerifiedRestoreExecutor restoreExecutor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticationKeyReference);
        ArgumentNullException.ThrowIfNull(restoreExecutor);
        cancellationToken.ThrowIfCancellationRequested();

        (string absoluteJournalPath, string absoluteStagingRoot, string absoluteBackupRoot) =
            ValidateRecoveryPaths(journalPath, stagingRoot, backupRoot);

        using CredentialSecret? secret = await _credentialVault
            .ReadAsync(authenticationKeyReference, cancellationToken)
            .ConfigureAwait(false);
        if (secret is null)
        {
            throw new InvalidOperationException("Restore recovery journal authentication key is not available in the credential vault.");
        }

        ReadOnlySpan<char> secretChars = secret.Value.Span;
        byte[] keyBytes = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(secretChars));
        Encoding.UTF8.GetBytes(secretChars, keyBytes);
        try
        {
            if (keyBytes.Length < 32)
            {
                throw new InvalidOperationException("Restore recovery journal authentication key must contain at least 256 bits of UTF-8 key material.");
            }

            var journalStore = new FileAuthenticatedRestoreRecoveryJournalStore(absoluteJournalPath, keyBytes);
            try
            {
                var stateResolver = new RestoreRecoveryStateResolver(journalStore, absoluteStagingRoot);
                var service = new RestoreRecoveryExecutionService(
                    stateResolver,
                    journalStore,
                    restoreExecutor,
                    absoluteBackupRoot,
                    _timeProvider);
                return new RestoreRecoveryExecutionComposition(service, journalStore);
            }
            catch
            {
                journalStore.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static (string JournalPath, string StagingRoot, string BackupRoot) ValidateRecoveryPaths(
        string journalPath,
        string stagingRoot,
        string backupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);

        if (!Path.IsPathFullyQualified(journalPath) ||
            !Path.IsPathFullyQualified(stagingRoot) ||
            !Path.IsPathFullyQualified(backupRoot))
        {
            throw new InvalidOperationException("Restore recovery paths must be explicitly fully qualified.");
        }

        string absoluteJournalPath = Path.GetFullPath(journalPath);
        string absoluteStagingRoot = Path.GetFullPath(stagingRoot);
        string absoluteBackupRoot = Path.GetFullPath(backupRoot);

        if (IsSameOrDescendant(absoluteJournalPath, absoluteStagingRoot) ||
            IsSameOrDescendant(absoluteJournalPath, absoluteBackupRoot) ||
            IsSameOrDescendant(absoluteStagingRoot, absoluteBackupRoot) ||
            IsSameOrDescendant(absoluteBackupRoot, absoluteStagingRoot))
        {
            throw new InvalidOperationException(
                "Restore recovery journal, staging, and backup paths must not overlap.");
        }

        return (absoluteJournalPath, absoluteStagingRoot, absoluteBackupRoot);
    }

    private static bool IsSameOrDescendant(string candidatePath, string rootPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(candidatePath, rootPath, comparison))
        {
            return true;
        }

        string rootWithSeparator = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootWithSeparator, comparison);
    }
}

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

            var journalStore = new FileAuthenticatedRestoreRecoveryJournalStore(journalPath, keyBytes);
            try
            {
                var stateResolver = new RestoreRecoveryStateResolver(journalStore, stagingRoot);
                var service = new RestoreRecoveryExecutionService(
                    stateResolver,
                    journalStore,
                    restoreExecutor,
                    backupRoot,
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
}

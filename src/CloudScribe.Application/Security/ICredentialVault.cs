using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Security;

public interface ICredentialVault
{
    ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default);
    ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default);
}

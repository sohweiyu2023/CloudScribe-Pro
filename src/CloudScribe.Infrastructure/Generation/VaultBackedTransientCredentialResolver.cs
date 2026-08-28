using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Resolves an explicitly configured provider credential reference from the local credential vault.
/// This resolver never discovers accounts, performs interactive login, refreshes tokens, or falls back
/// to ambient credentials. The stored material must already be a currently valid bearer access token.
/// </summary>
public sealed class VaultBackedTransientCredentialResolver(ICredentialVault credentialVault) : ITransientCredentialResolver
{
    private readonly ICredentialVault _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));

    public async ValueTask<string> ResolveAccessTokenAsync(
        string credentialReferenceId,
        CancellationToken cancellationToken)
    {
        CredentialReference reference = new(credentialReferenceId);
        using CredentialSecret? secret = await _credentialVault.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            throw new InvalidOperationException("The explicitly configured provider credential reference is unavailable.");
        }

        ReadOnlySpan<char> value = secret.Value.Span;
        if (value.IsEmpty || IsAllWhitespace(value))
        {
            throw new InvalidOperationException("The explicitly configured provider credential contains no bearer access token.");
        }

        return new string(value);
    }

    private static bool IsAllWhitespace(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }
}

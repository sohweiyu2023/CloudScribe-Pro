using System.Security.Cryptography;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VaultBackedGenerationPrivateCacheKeyProvider : IGenerationPrivateCacheKeyProvider, IDisposable
{
    private const int KeyBytes = 32;
    private static readonly CredentialReference CacheKeyReference = new("cache-private-hmac-v2-23");
    private readonly ICredentialVault _credentialVault;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VaultBackedGenerationPrivateCacheKeyProvider(ICredentialVault credentialVault)
    {
        _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
    }

    public async ValueTask<GenerationPrivateCacheKeyMaterial> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var existing = await _credentialVault.ReadAsync(CacheKeyReference, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return Decode(existing.Value.Span);
            }

            var key = RandomNumberGenerator.GetBytes(KeyBytes);
            var encoded = new char[44];
            try
            {
                if (!Convert.TryToBase64Chars(key, encoded, out var written) || written != encoded.Length)
                {
                    throw new CryptographicException("Could not encode the private cache HMAC key for OS-protected storage.");
                }

                await _credentialVault.StoreAsync(CacheKeyReference, encoded, cancellationToken).ConfigureAwait(false);
                return new GenerationPrivateCacheKeyMaterial(key);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
            finally
            {
                Array.Clear(encoded);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static GenerationPrivateCacheKeyMaterial Decode(ReadOnlySpan<char> encoded)
    {
        Span<byte> decoded = stackalloc byte[KeyBytes + 16];
        if (!Convert.TryFromBase64Chars(encoded, decoded, out var written) || written != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new InvalidDataException("OS credential storage contained invalid CloudScribe private cache HMAC key material.");
        }

        var key = decoded[..KeyBytes].ToArray();
        CryptographicOperations.ZeroMemory(decoded);
        return new GenerationPrivateCacheKeyMaterial(key);
    }
}

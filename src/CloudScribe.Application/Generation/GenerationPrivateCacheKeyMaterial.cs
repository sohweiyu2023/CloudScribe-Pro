using System.Security.Cryptography;

namespace CloudScribe.Application.Generation;

public sealed class GenerationPrivateCacheKeyMaterial : IDisposable
{
    private byte[]? _buffer;

    public GenerationPrivateCacheKeyMaterial(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < 32)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new ArgumentException("Private cache HMAC key material must contain at least 256 bits.", nameof(buffer));
        }

        _buffer = buffer;
    }

    public ReadOnlySpan<byte> Span => _buffer ?? throw new ObjectDisposedException(nameof(GenerationPrivateCacheKeyMaterial));

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}

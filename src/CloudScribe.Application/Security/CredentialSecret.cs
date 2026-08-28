namespace CloudScribe.Application.Security;

public sealed class CredentialSecret : IDisposable
{
    private char[]? _buffer;

    public CredentialSecret(char[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("Credential secrets cannot be empty.", nameof(buffer));
        }
        _buffer = buffer;
    }

    public ReadOnlyMemory<char> Value => _buffer ?? throw new ObjectDisposedException(nameof(CredentialSecret));

    public void Dispose()
    {
        char[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            Array.Clear(buffer);
        }
    }
}

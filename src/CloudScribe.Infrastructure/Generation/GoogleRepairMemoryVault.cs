using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Short-lived candidate material only; never registered as the application credential vault.</summary>
internal sealed class GoogleRepairMemoryVault : ICredentialVault, IDisposable
{
    private readonly Dictionary<string, char[]> _items = new(StringComparer.Ordinal);
    private bool _disposed;

    public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_items.ContainsKey(reference.TargetName)) throw new InvalidOperationException("Candidate slot already exists.");
        _items.Add(reference.TargetName, secret.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryGetValue(reference.TargetName, out var value) ? new CredentialSecret(value.ToArray()) : null);
    }

    public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.Remove(reference.TargetName, out var value)) return ValueTask.FromResult(false);
        Array.Clear(value);
        return ValueTask.FromResult(true);
    }

    public void Dispose()
    {
        foreach (var value in _items.Values) Array.Clear(value);
        _items.Clear();
        _disposed = true;
    }
}

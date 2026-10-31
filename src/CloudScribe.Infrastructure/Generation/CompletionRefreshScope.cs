using System.Text;

namespace CloudScribe.Infrastructure.Generation;

public sealed class CompletionRefreshScope : IDisposable
{
    private readonly CompletionRefreshLease? _lease;
    private readonly bool _owner;
    private readonly Action? _clear;
    private bool _success;
    private bool _disposed;

    internal CompletionRefreshScope(CompletionRefreshLease? lease, bool owner, Action? clear)
    { _lease = lease; _owner = owner; _clear = clear; }

    public void Succeed() => _success = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_owner) _lease!.Finish(_success); }
        finally { _clear?.Invoke(); }
    }
}

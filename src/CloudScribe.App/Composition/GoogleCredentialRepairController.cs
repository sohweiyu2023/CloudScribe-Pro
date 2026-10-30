using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Private candidate lifetime and separate exact local-write consent; never automatic verification or commit.</summary>
public sealed class GoogleCredentialRepairController(GoogleCredentialRepairService service, string accountId) : IDisposable
{
    private char[] _candidate = [];
    private string _email = string.Empty;
    private long _version;
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private readonly System.Threading.Lock _stateGate = new();
    private GoogleCredentialRepairReview? _review;
    private bool _acknowledged;
    public GoogleCredentialRepairReview? Review { get { lock (_stateGate) return _review; } }
    public bool Acknowledged
    {
        get { lock (_stateGate) return _acknowledged; }
        set { lock (_stateGate) _acknowledged = value && _review is not null; }
    }
    public bool MutationAttempted { get; private set; }
    public bool Committed { get; private set; }
    public string IntendedEmail
    {
        get { lock (_stateGate) return _email; }
        set { lock (_stateGate) { Invalidate(); _email = value; } }
    }

    public void SelectCandidate(ReadOnlyMemory<char> json)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Invalidate(); Array.Clear(_candidate); _candidate = [];
            if (json.IsEmpty || json.Length > 16 * 1024) throw new InvalidDataException("Select one bounded service-account JSON file.");
            _candidate = json.ToArray();
        }
    }

    public void BeginCandidateSelection()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Invalidate(); Array.Clear(_candidate); _candidate = [];
        }
    }

    public async Task VerifyAsync(CancellationToken token = default)
    {
        long version;
        char[] owned;
        string email;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Invalidate(); version = _version; owned = _candidate.ToArray(); email = _email; _operation = operation;
            Array.Clear(_candidate); _candidate = [];
        }
        try
        {
            var review = await service.PrepareAsync(accountId, email, owned, operation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (_disposed || version != _version)
                {
                    service.Discard(review.ReviewId);
                    throw new InvalidOperationException("Candidate or identity changed during verification; no local write was approved.");
                }
                _review = review;
            }
        }
        finally { Array.Clear(owned); lock (_stateGate) { if (ReferenceEquals(_operation, operation)) _operation = null; } }
    }

    public async Task ConfirmAsync(CancellationToken token = default)
    {
        GoogleCredentialRepairReview review;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            review = _review ?? throw new InvalidOperationException("Verify the exact candidate first.");
            if (!_acknowledged) throw new InvalidOperationException("Explicit local credential-write confirmation is required.");
            _review = null; _acknowledged = false; MutationAttempted = true;
            Array.Clear(_candidate); _candidate = []; _operation = operation;
        }
        try
        {
            await service.ConfirmAsync(review.ReviewId, review.ReviewSha256, operation.Token).ConfigureAwait(false);
            Committed = true;
        }
        finally { lock (_stateGate) { if (ReferenceEquals(_operation, operation)) _operation = null; } }
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _operation?.Cancel();
        if (_review is { } review) service.Discard(review.ReviewId);
        _review = null; _acknowledged = false;
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true; Invalidate(); Array.Clear(_candidate); _candidate = [];
        }
    }
}

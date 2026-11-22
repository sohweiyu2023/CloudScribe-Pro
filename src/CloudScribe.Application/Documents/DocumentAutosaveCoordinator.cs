using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Documents;

/// <summary>
/// Coalesces rapid editor mutations into one durable autosave while keeping explicit checkpoints separate.
/// The UI owns the current document/version snapshot; this coordinator owns only bounded debounce timing.
/// </summary>
public sealed class DocumentAutosaveCoordinator : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly IDocumentLibrary _documentLibrary;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _pendingCancellation;
    private Task<DocumentSnapshot?> _pendingTask = Task.FromResult<DocumentSnapshot?>(null);
    private (Guid DocumentId, long ConcurrencyVersion)? _lastCompletedAutosave;
    private int _disposed;

    public DocumentAutosaveCoordinator(IDocumentLibrary documentLibrary, TimeProvider timeProvider)
    {
        _documentLibrary = documentLibrary ?? throw new ArgumentNullException(nameof(documentLibrary));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<DocumentSnapshot?> QueueAsync(
        DocumentSaveRequest request,
        TimeSpan debounce,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(debounce, TimeSpan.Zero);

        CancellationTokenSource next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        Task<DocumentSnapshot?> pending;
        lock (_gate)
        {
            previous = _pendingCancellation;
            _pendingCancellation = next;
            pending = SaveAfterDelayAsync(request, debounce, next);
            _pendingTask = pending;
        }

        previous?.Cancel();
        return pending;
    }

    public async Task<DocumentSnapshot> SaveCheckpointAsync(
        DocumentSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        Task<DocumentSnapshot?> pending;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _pendingCancellation;
            pending = _pendingTask;
            _pendingCancellation = null;
            _pendingTask = Task.FromResult<DocumentSnapshot?>(null);
        }

        cancellation?.Cancel();
        DocumentSnapshot? completedAutosave = await DrainPendingAutosaveAsync(pending).ConfigureAwait(false);
        long effectiveVersion = request.ExpectedConcurrencyVersion;
        if (completedAutosave is not null
            && completedAutosave.Id == request.DocumentId
            && completedAutosave.ConcurrencyVersion > effectiveVersion)
        {
            effectiveVersion = completedAutosave.ConcurrencyVersion;
        }

        lock (_gate)
        {
            if (_lastCompletedAutosave is { } last
                && last.DocumentId == request.DocumentId
                && last.ConcurrencyVersion > effectiveVersion)
            {
                effectiveVersion = last.ConcurrencyVersion;
            }
        }

        DocumentSaveRequest effectiveRequest = effectiveVersion != request.ExpectedConcurrencyVersion
            ? request with { ExpectedConcurrencyVersion = effectiveVersion }
            : request;
        DocumentSnapshot saved = await _documentLibrary.SaveAsync(
            effectiveRequest with { RevisionKind = DocumentRevisionKind.Checkpoint },
            cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_lastCompletedAutosave is { } last && last.DocumentId == saved.Id)
            {
                _lastCompletedAutosave = null;
            }
        }

        return saved;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task<DocumentSnapshot?> pending;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _pendingCancellation;
            pending = _pendingTask;
            _pendingCancellation = null;
            _pendingTask = Task.FromResult<DocumentSnapshot?>(null);
        }

        cancellation?.Cancel();
        _ = await DrainPendingAutosaveAsync(pending).ConfigureAwait(false);
    }

    private static async Task<DocumentSnapshot?> DrainPendingAutosaveAsync(Task<DocumentSnapshot?> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Pending autosave errors are already observed by the caller of QueueAsync;
            // they must not block an explicit checkpoint retry or coordinator disposal.
            return null;
        }
    }

    private async Task<DocumentSnapshot?> SaveAfterDelayAsync(
        DocumentSaveRequest request,
        TimeSpan debounce,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(debounce, _timeProvider, cancellation.Token).ConfigureAwait(false);
            DocumentSnapshot saved = await _documentLibrary.SaveAsync(
                request with { RevisionKind = DocumentRevisionKind.Autosave },
                cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _lastCompletedAutosave = (saved.Id, saved.ConcurrencyVersion);
            }

            return saved;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCancellation, cancellation))
                {
                    _pendingCancellation = null;
                    _pendingTask = Task.FromResult<DocumentSnapshot?>(null);
                }
            }

            cancellation.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

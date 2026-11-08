namespace CloudScribe.Application.Batch;

public interface IBatchQueueStore
{
    Task<NarrationBatch?> LoadAsync(Guid batchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NarrationBatch>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves atomically and returns the stored copy with the next revision.
    /// Throws <see cref="BatchConcurrencyException"/> if someone else saved since <paramref name="queue"/> was read.</summary>
    Task<NarrationBatch> SaveAsync(NarrationBatch queue, CancellationToken cancellationToken = default);

    /// <summary>Exclusive, crash-safe ownership of one batch's run (released by the OS if the app dies).
    /// Returns null when another window or process already runs this batch.</summary>
    IDisposable? TryAcquireRunLock(Guid batchId);
}

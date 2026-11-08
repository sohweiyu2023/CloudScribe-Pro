namespace CloudScribe.Application.Batch;

/// <summary>Narrates one imported document into one MP3 using the app's existing services.
/// Contract: before the first paid request for a file, call the <c>recordJob</c> callback with the durable job
/// identity and await it. When <see cref="BatchItem.JobId"/> is already set, continue or reconcile that same job;
/// never create a second paid job for the same file. Respect cancellation only at safe boundaries.</summary>
public interface IBatchNarrator
{
    /// <summary>True when a spending authority currently covers this batch; otherwise the reason to show.</summary>
    Task<(bool Authorized, string Explanation)> CheckAuthorityAsync(NarrationBatch queue, CancellationToken cancellationToken = default);

    Task<BatchNarrationOutcome> NarrateAsync(NarrationBatch queue, BatchItem item, Func<Guid, Task> recordJob,
        IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Checks that a completed output still exists unchanged, so completed files are reused, not regenerated.</summary>
    Task<bool> VerifyOutputAsync(BatchItem item, CancellationToken cancellationToken = default);
}

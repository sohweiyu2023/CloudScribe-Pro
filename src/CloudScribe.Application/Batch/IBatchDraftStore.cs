namespace CloudScribe.Application.Batch;

/// <summary>Keeps the one unsaved draft for this profile.</summary>
public interface IBatchDraftStore
{
    Task<BatchDraft?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(BatchDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Removes the draft (after it was saved as a batch, or discarded by the user).</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

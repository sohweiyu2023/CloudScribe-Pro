using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public interface IGoogleGenerationPersistedQueueStateStore
{
    Task<GoogleGenerationPersistedQueueState?> LoadAsync(
        string accountId,
        string operationStableId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        GoogleGenerationPersistedQueueState state,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimSubmissionAsync(GoogleGenerationPersistedQueueState claim, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Atomic Google dispatch persistence is not configured.");

    Task SaveOutcomeAsync(GoogleGenerationPersistedQueueState outcome, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Owned Google outcome persistence is not configured.");
}

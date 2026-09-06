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
}

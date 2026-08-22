using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public interface IGenerationRecoveryStore
{
    Task SaveAsync(GenerationRecoverySnapshot snapshot, CancellationToken cancellationToken = default);

    Task<GenerationRecoverySnapshot?> ReadAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationRecoverySnapshot>> ListRecoverableAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface IGenerationSegmentProgressStore
{
    Task SaveAsync(GenerationSegmentProgress progress, CancellationToken cancellationToken = default);

    Task<GenerationSegmentProgress?> ReadAsync(Guid jobId, string segmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationSegmentProgress>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface IGenerationSegmentCache
{
    Task<bool> ContainsAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default);

    Task<byte[]?> ReadAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default);

    Task StoreAsync(ContentAddressedSegmentKey key, ReadOnlyMemory<byte> mediaBytes, CancellationToken cancellationToken = default);
}

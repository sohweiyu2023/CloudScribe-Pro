using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public interface IGenerationSegmentProgressStore
{
    Task SaveAsync(GenerationSegmentProgress progress, CancellationToken cancellationToken = default);

    Task<GenerationSegmentProgress?> ReadAsync(Guid jobId, string segmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationSegmentProgress>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

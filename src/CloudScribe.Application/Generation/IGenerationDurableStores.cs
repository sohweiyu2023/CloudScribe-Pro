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

[Flags]
public enum GenerationCacheEntryProtection
{
    None = 0,
    Active = 1,
    Pinned = 2,
    Referenced = 4,
    UnresolvedSubmission = 8,
}

public sealed record GenerationCacheTrimResult(long BytesBefore, long BytesAfter, int EntriesEvicted, int EntriesProtected);

public sealed record GenerationCacheClearResult(int EntriesRemoved, int EntriesProtected, long BytesRemoved);

public interface IGenerationCacheLifecycle
{
    Task SetProtectionAsync(
        ContentAddressedSegmentKey key,
        GenerationCacheEntryProtection protection,
        CancellationToken cancellationToken = default);

    Task<GenerationCacheTrimResult> TrimAsync(
        long? maximumBytes = null,
        CancellationToken cancellationToken = default);

    Task<GenerationCacheClearResult> ClearUnprotectedAsync(CancellationToken cancellationToken = default);
}

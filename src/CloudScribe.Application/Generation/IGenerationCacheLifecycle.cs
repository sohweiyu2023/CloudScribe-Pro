using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

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

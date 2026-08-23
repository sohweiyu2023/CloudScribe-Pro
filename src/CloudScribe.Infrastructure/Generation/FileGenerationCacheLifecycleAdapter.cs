using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using ApplicationProtection = CloudScribe.Application.Generation.GenerationCacheEntryProtection;
using InfrastructureProtection = CloudScribe.Infrastructure.Generation.GenerationCacheEntryProtection;

namespace CloudScribe.Infrastructure.Generation;

public sealed class FileGenerationCacheLifecycleAdapter : IGenerationCacheLifecycle
{
    private readonly FileGenerationSegmentCache _cache;

    public FileGenerationCacheLifecycleAdapter(FileGenerationSegmentCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public Task SetProtectionAsync(
        ContentAddressedSegmentKey key,
        ApplicationProtection protection,
        CancellationToken cancellationToken = default) =>
        _cache.SetProtectionAsync(key, Map(protection), cancellationToken);

    public async Task<CloudScribe.Application.Generation.GenerationCacheTrimResult> TrimAsync(
        long? maximumBytes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _cache.TrimAsync(maximumBytes, cancellationToken).ConfigureAwait(false);
        return new(result.BytesBefore, result.BytesAfter, result.EntriesEvicted, result.EntriesProtected);
    }

    public async Task<CloudScribe.Application.Generation.GenerationCacheClearResult> ClearUnprotectedAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _cache.ClearUnprotectedAsync(cancellationToken).ConfigureAwait(false);
        return new(result.EntriesRemoved, result.EntriesProtected, result.BytesRemoved);
    }

    private static InfrastructureProtection Map(ApplicationProtection protection)
    {
        const ApplicationProtection allowed =
            ApplicationProtection.Active |
            ApplicationProtection.Pinned |
            ApplicationProtection.Referenced |
            ApplicationProtection.UnresolvedSubmission;

        if ((protection & ~allowed) != 0)
            throw new ArgumentOutOfRangeException(nameof(protection));

        var mapped = InfrastructureProtection.None;
        if ((protection & ApplicationProtection.Active) != 0) mapped |= InfrastructureProtection.Active;
        if ((protection & ApplicationProtection.Pinned) != 0) mapped |= InfrastructureProtection.Pinned;
        if ((protection & ApplicationProtection.Referenced) != 0) mapped |= InfrastructureProtection.Referenced;
        if ((protection & ApplicationProtection.UnresolvedSubmission) != 0) mapped |= InfrastructureProtection.UnresolvedSubmission;
        return mapped;
    }
}

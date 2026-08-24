using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public static class GenerationCacheLifecycleTransitionValidator
{
    public static GenerationProjectCacheState ValidateTransition(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized)
    {
        key.Validate();
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        if (!cacheEntryMaterialized && (next.Pinned || next.Referenced))
            throw new InvalidOperationException("A cache entry cannot become pinned or referenced before media is materialized.");

        if (previous.UnresolvedSubmission && !next.UnresolvedSubmission && next.Active)
            throw new InvalidOperationException("An unresolved submission cannot be cleared while the generation remains active.");

        if (previous.Referenced && !next.Referenced && next.Pinned)
            throw new InvalidOperationException("Referenced protection cannot be silently removed by an unrelated pin transition.");

        if (previous.Pinned && !next.Pinned && next.Referenced)
            throw new InvalidOperationException("Pinned protection cannot be silently removed by an unrelated reference transition.");

        return next;
    }
}

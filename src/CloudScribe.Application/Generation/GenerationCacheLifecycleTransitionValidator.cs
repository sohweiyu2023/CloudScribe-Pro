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

        if (!cacheEntryMaterialized && (next.Active || next.Pinned || next.Referenced || next.UnresolvedSubmission))
            throw new InvalidOperationException("Cache protection cannot be persisted before the media cache entry is materialized; unresolved generation state must remain in durable job progress instead.");

        if (previous.UnresolvedSubmission && !next.UnresolvedSubmission && next.Active)
            throw new InvalidOperationException("An unresolved submission cannot be cleared while the generation remains active.");

        if (previous.Referenced && !next.Referenced && next.Pinned)
            throw new InvalidOperationException("Referenced protection cannot be silently removed by an unrelated pin transition.");

        if (previous.Pinned && !next.Pinned && next.Referenced)
            throw new InvalidOperationException("Pinned protection cannot be silently removed by an unrelated reference transition.");

        if (!cacheEntryMaterialized && (previous.Active || previous.Pinned || previous.Referenced || previous.UnresolvedSubmission))
            throw new InvalidOperationException("Persisted cache protection claims a materialized entry that is now absent; reconcile cache metadata before changing lifecycle state.");

        return next;
    }
}

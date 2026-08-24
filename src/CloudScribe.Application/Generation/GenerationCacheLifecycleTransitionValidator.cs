using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public enum GenerationSubmissionResolutionEvidence
{
    None = 0,
    ProviderConfirmedTerminal = 1,
    DurableReceiptReconciled = 2
}

public enum GenerationReferenceResolutionEvidence
{
    None = 0,
    ReleaseReferenceRemoved = 1
}

public static class GenerationCacheLifecycleTransitionValidator
{
    public static GenerationProjectCacheState ValidateTransition(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized) =>
        ValidateTransition(
            key,
            previous,
            next,
            cacheEntryMaterialized,
            GenerationSubmissionResolutionEvidence.None,
            GenerationReferenceResolutionEvidence.None);

    public static GenerationProjectCacheState ValidateTransition(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        GenerationSubmissionResolutionEvidence resolutionEvidence) =>
        ValidateTransition(
            key,
            previous,
            next,
            cacheEntryMaterialized,
            resolutionEvidence,
            GenerationReferenceResolutionEvidence.None);

    public static GenerationProjectCacheState ValidateTransition(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        GenerationSubmissionResolutionEvidence resolutionEvidence,
        GenerationReferenceResolutionEvidence referenceEvidence)
    {
        key.Validate();
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        if (!Enum.IsDefined(resolutionEvidence))
            throw new ArgumentOutOfRangeException(nameof(resolutionEvidence));
        if (!Enum.IsDefined(referenceEvidence))
            throw new ArgumentOutOfRangeException(nameof(referenceEvidence));

        if (!cacheEntryMaterialized && (next.Active || next.Pinned || next.Referenced || next.UnresolvedSubmission))
            throw new InvalidOperationException("Cache protection cannot be persisted before the media cache entry is materialized; unresolved generation state must remain in durable job progress instead.");

        if (previous.UnresolvedSubmission && !next.UnresolvedSubmission)
        {
            if (next.Active)
                throw new InvalidOperationException("An unresolved submission cannot be cleared while the generation remains active.");
            if (resolutionEvidence == GenerationSubmissionResolutionEvidence.None)
                throw new InvalidOperationException("Clearing unresolved cache protection requires explicit provider-terminal or durable-receipt reconciliation evidence.");
        }
        else if (resolutionEvidence != GenerationSubmissionResolutionEvidence.None)
        {
            throw new InvalidOperationException("Submission resolution evidence is only valid when clearing a previously unresolved submission.");
        }

        if (previous.Referenced && !next.Referenced)
        {
            if (referenceEvidence != GenerationReferenceResolutionEvidence.ReleaseReferenceRemoved)
                throw new InvalidOperationException("Referenced cache protection may be cleared only with explicit evidence that the durable release reference was removed.");
        }
        else if (referenceEvidence != GenerationReferenceResolutionEvidence.None)
        {
            throw new InvalidOperationException("Reference removal evidence is only valid when clearing previously referenced cache protection.");
        }

        if (previous.Pinned && !next.Pinned && next.Referenced)
            throw new InvalidOperationException("Pinned protection cannot be silently removed by an unrelated reference transition.");

        if (!cacheEntryMaterialized && (previous.Active || previous.Pinned || previous.Referenced || previous.UnresolvedSubmission))
            throw new InvalidOperationException("Persisted cache protection claims a materialized entry that is now absent; reconcile cache metadata before changing lifecycle state.");

        return next;
    }
}

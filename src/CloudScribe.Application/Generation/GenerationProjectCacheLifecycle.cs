using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class GenerationProjectCacheLifecycle
{
    private readonly GenerationSegmentCacheLifecycleCoordinator _coordinator;

    public GenerationProjectCacheLifecycle(GenerationSegmentCacheLifecycleCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public Task SetPinnedAsync(ContentAddressedSegmentKey key, bool pinned, CancellationToken cancellationToken = default) =>
        RejectUnsafePartialUpdate(key, cancellationToken, nameof(SetPinnedAsync), pinned.ToString());

    public Task SetReferencedAsync(ContentAddressedSegmentKey key, bool referenced, CancellationToken cancellationToken = default) =>
        RejectUnsafePartialUpdate(key, cancellationToken, nameof(SetReferencedAsync), referenced.ToString());

    public Task SetProjectStateAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState state,
        CancellationToken cancellationToken = default) =>
        RejectUnsafePartialUpdate(key, cancellationToken, nameof(SetProjectStateAsync), state.ToString());

    public Task SetValidatedTransitionAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        CancellationToken cancellationToken = default) =>
        SetValidatedTransitionAsync(
            key, previous, next, cacheEntryMaterialized,
            GenerationSubmissionResolutionEvidence.None,
            GenerationReferenceResolutionEvidence.None,
            GenerationPinResolutionEvidence.None,
            cancellationToken);

    public Task SetValidatedTransitionAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        GenerationSubmissionResolutionEvidence resolutionEvidence,
        CancellationToken cancellationToken = default) =>
        SetValidatedTransitionAsync(
            key, previous, next, cacheEntryMaterialized,
            resolutionEvidence,
            GenerationReferenceResolutionEvidence.None,
            GenerationPinResolutionEvidence.None,
            cancellationToken);

    public Task SetValidatedTransitionAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        GenerationSubmissionResolutionEvidence resolutionEvidence,
        GenerationReferenceResolutionEvidence referenceEvidence,
        CancellationToken cancellationToken = default) =>
        SetValidatedTransitionAsync(
            key, previous, next, cacheEntryMaterialized,
            resolutionEvidence,
            referenceEvidence,
            GenerationPinResolutionEvidence.None,
            cancellationToken);

    public async Task SetValidatedTransitionAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        GenerationSubmissionResolutionEvidence resolutionEvidence,
        GenerationReferenceResolutionEvidence referenceEvidence,
        GenerationPinResolutionEvidence pinEvidence,
        CancellationToken cancellationToken = default)
    {
        var validated = GenerationCacheLifecycleTransitionValidator.ValidateTransition(
            key,
            previous,
            next,
            cacheEntryMaterialized,
            resolutionEvidence,
            referenceEvidence,
            pinEvidence);

        await ApplyStateAsync(key, validated, cancellationToken).ConfigureAwait(false);
    }

    private Task RejectUnsafePartialUpdate(
        ContentAddressedSegmentKey key,
        CancellationToken cancellationToken,
        string operationName,
        string requestedState)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            $"{_coordinator.GetType().Name} rejects unsafe partial cache lifecycle operation '{operationName}' ({requestedState}). Use SetValidatedTransitionAsync with the complete previous and next lifecycle state plus required evidence.");
    }

    private Task ApplyStateAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState state,
        CancellationToken cancellationToken) =>
        _coordinator.SetCompositeProtectionAsync(
            key,
            state.Active,
            state.Pinned,
            state.Referenced,
            state.UnresolvedSubmission,
            cancellationToken);
}

using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationProjectCacheState(
    bool Active,
    bool Pinned,
    bool Referenced,
    bool UnresolvedSubmission);

public sealed class GenerationProjectCacheLifecycle
{
    private readonly GenerationSegmentCacheLifecycleCoordinator _coordinator;

    public GenerationProjectCacheLifecycle(GenerationSegmentCacheLifecycleCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public Task SetPinnedAsync(ContentAddressedSegmentKey key, bool pinned, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A partial project cache lifecycle update is unsafe. Use SetValidatedTransitionAsync with the complete previous and next lifecycle state.");

    public Task SetReferencedAsync(ContentAddressedSegmentKey key, bool referenced, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A partial project cache lifecycle update is unsafe. Use SetValidatedTransitionAsync with the complete previous and next lifecycle state.");

    public Task SetProjectStateAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState state,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("An unvalidated project cache lifecycle write is unsafe. Use SetValidatedTransitionAsync with materialization evidence.");

    public async Task SetValidatedTransitionAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState previous,
        GenerationProjectCacheState next,
        bool cacheEntryMaterialized,
        CancellationToken cancellationToken = default)
    {
        var validated = GenerationCacheLifecycleTransitionValidator.ValidateTransition(
            key,
            previous,
            next,
            cacheEntryMaterialized);

        await ApplyStateAsync(key, validated, cancellationToken).ConfigureAwait(false);
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

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
        throw new InvalidOperationException("A partial project cache lifecycle update is unsafe. Use SetProjectStateAsync with the complete current lifecycle state.");

    public Task SetReferencedAsync(ContentAddressedSegmentKey key, bool referenced, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A partial project cache lifecycle update is unsafe. Use SetProjectStateAsync with the complete current lifecycle state.");

    public async Task SetProjectStateAsync(
        ContentAddressedSegmentKey key,
        GenerationProjectCacheState state,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        ArgumentNullException.ThrowIfNull(state);
        await _coordinator.SetCompositeProtectionAsync(
            key,
            state.Active,
            state.Pinned,
            state.Referenced,
            state.UnresolvedSubmission,
            cancellationToken).ConfigureAwait(false);
    }
}

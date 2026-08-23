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
        throw new InvalidOperationException("A partial project cache lifecycle update is unsafe. Use SetProjectStateAsync with the complete current lifecycle state.");

    public Task SetReferencedAsync(ContentAddressedSegmentKey key, bool referenced, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A partial project cache lifecycle update is unsafe. Use SetProjectStateAsync with the complete current lifecycle state.");

    public async Task SetProjectStateAsync(
        ContentAddressedSegmentKey key,
        bool active,
        bool pinned,
        bool referenced,
        bool unresolvedSubmission,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        await _coordinator.SetCompositeProtectionAsync(
            key,
            active,
            pinned,
            referenced,
            unresolvedSubmission,
            cancellationToken).ConfigureAwait(false);
    }
}

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
        pinned
            ? _coordinator.MarkPinnedAsync(key, cancellationToken)
            : _coordinator.MarkCompletedAsync(key, cancellationToken);

    public Task SetReferencedAsync(ContentAddressedSegmentKey key, bool referenced, CancellationToken cancellationToken = default) =>
        referenced
            ? _coordinator.MarkReferencedAsync(key, cancellationToken)
            : _coordinator.MarkCompletedAsync(key, cancellationToken);

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

using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class GenerationSegmentCacheLifecycleCoordinator
{
    private readonly IGenerationCacheLifecycle _cacheLifecycle;

    public GenerationSegmentCacheLifecycleCoordinator(IGenerationCacheLifecycle cacheLifecycle)
    {
        _cacheLifecycle = cacheLifecycle ?? throw new ArgumentNullException(nameof(cacheLifecycle));
    }

    public Task MarkActiveAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
        SetStateAsync(key, GenerationCacheLifecycleState.Active, cancellationToken);

    public Task MarkPinnedAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
        SetStateAsync(key, GenerationCacheLifecycleState.Pinned, cancellationToken);

    public Task MarkReferencedAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
        SetStateAsync(key, GenerationCacheLifecycleState.Referenced, cancellationToken);

    public Task MarkUnresolvedSubmissionAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
        SetStateAsync(key, GenerationCacheLifecycleState.UnresolvedSubmission, cancellationToken);

    public Task MarkCompletedAsync(ContentAddressedSegmentKey key, CancellationToken cancellationToken = default) =>
        SetStateAsync(key, GenerationCacheLifecycleState.Completed, cancellationToken);

    public Task SetCompositeProtectionAsync(
        ContentAddressedSegmentKey key,
        bool active,
        bool pinned,
        bool referenced,
        bool unresolvedSubmission,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        var protection = GenerationCacheProtectionPolicy.Combine(active, pinned, referenced, unresolvedSubmission);
        return _cacheLifecycle.SetProtectionAsync(key, protection, cancellationToken);
    }

    private Task SetStateAsync(
        ContentAddressedSegmentKey key,
        GenerationCacheLifecycleState state,
        CancellationToken cancellationToken)
    {
        key.Validate();
        var protection = GenerationCacheProtectionPolicy.ForState(state);
        return _cacheLifecycle.SetProtectionAsync(key, protection, cancellationToken);
    }
}

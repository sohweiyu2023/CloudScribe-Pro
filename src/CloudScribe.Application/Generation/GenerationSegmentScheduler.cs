using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class GenerationSegmentScheduler
{
    private readonly GenerationSegmentExecutor _executor;
    private readonly IGenerationSegmentCache _cache;
    private readonly IGenerationSegmentProgressStore _progressStore;
    private readonly GenerationExecutionPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly GenerationSegmentCacheLifecycleCoordinator? _cacheLifecycleCoordinator;

    public GenerationSegmentScheduler(
        GenerationSegmentExecutor executor,
        IGenerationSegmentCache cache,
        IGenerationSegmentProgressStore progressStore,
        GenerationExecutionPolicy policy,
        TimeProvider? timeProvider = null,
        GenerationSegmentCacheLifecycleCoordinator? cacheLifecycleCoordinator = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheLifecycleCoordinator = cacheLifecycleCoordinator;
        _concurrencyGate = new SemaphoreSlim(_policy.MaximumConcurrentRequests, _policy.MaximumConcurrentRequests);
    }

    public async Task<IReadOnlyList<GenerationScheduledSegmentResult>> ExecuteReadyAsync(
        IReadOnlyList<GenerationScheduledSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var tasks = segments
            .OrderBy(static segment => segment.SegmentIndex)
            .ThenBy(static segment => segment.SegmentId, StringComparer.Ordinal)
            .Select(segment => ExecuteOneBoundedAsync(segment, cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<GenerationScheduledSegmentResult> ExecuteOneBoundedAsync(
        GenerationScheduledSegment segment,
        CancellationToken cancellationToken)
    {
        Validate(segment);
        await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteOneAsync(segment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private async Task<GenerationScheduledSegmentResult> ExecuteOneAsync(
        GenerationScheduledSegment segment,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var progress = await LoadProgressAsync(segment, now, cancellationToken).ConfigureAwait(false);
        EnsureProgressIdentity(segment, progress);
        if (progress.IsTerminal)
            return new GenerationScheduledSegmentResult(progress, null);
        if (progress.RequiresReconciliation)
            return await ReconcileOnlyAsync(segment, progress, cancellationToken).ConfigureAwait(false);
        if (progress.State == GenerationSegmentProgressState.RetryWait &&
            progress.NotBeforeUnixMilliseconds is { } notBefore && now < notBefore)
            return new GenerationScheduledSegmentResult(progress, null);

        // Cache reuse stays solely in GenerationSegmentExecutor so private trust, Force Fresh,
        // structural validation, and metadata-qualified eligibility cannot be bypassed here.
        var cacheKey = await _executor.CreatePrivateCacheKeyAsync(segment.Request, cancellationToken).ConfigureAwait(false);
        await SetProtectionIfMaterializedAsync(cacheKey, GenerationCacheLifecycleState.Active, cancellationToken).ConfigureAwait(false);
        progress = progress.MarkSubmissionStarted(now);
        await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);

        var submission = await SubmitWithUnknownOutcomeProtectionAsync(
            segment, cacheKey, progress, cancellationToken).ConfigureAwait(false);
        if (submission.Result is null)
            return new GenerationScheduledSegmentResult(submission.Progress, null);

        return await ApplySubmissionResultAsync(
            segment, cacheKey, submission.Progress, submission.Result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GenerationSegmentProgress> LoadProgressAsync(
        GenerationScheduledSegment segment,
        long now,
        CancellationToken cancellationToken) =>
        await _progressStore.ReadAsync(segment.JobId, segment.SegmentId, cancellationToken).ConfigureAwait(false)
        ?? new GenerationSegmentProgress(
            segment.JobId,
            segment.SegmentId,
            segment.SegmentIndex,
            segment.Request.IdempotencyKey,
            GenerationSegmentProgressState.Pending,
            0,
            now);

    private static void EnsureProgressIdentity(
        GenerationScheduledSegment segment,
        GenerationSegmentProgress progress)
    {
        if (!string.Equals(progress.IdempotencyKey, segment.Request.IdempotencyKey, StringComparison.Ordinal) ||
            progress.SegmentIndex != segment.SegmentIndex)
        {
            throw new InvalidOperationException("Persisted segment progress does not match the immutable scheduled segment identity.");
        }
    }

    private async Task<(GenerationSegmentProgress Progress, GenerationSegmentExecutionResult? Result)> SubmitWithUnknownOutcomeProtectionAsync(
        GenerationScheduledSegment segment,
        ContentAddressedSegmentKey cacheKey,
        GenerationSegmentProgress progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _executor.ExecuteAsync(segment.Request, cancellationToken).ConfigureAwait(false);
            return (progress, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress = progress.MarkSubmissionUnknown(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                null,
                "segment.submission.cancelled-outcome-unknown");
            await PersistUnknownOutcomeAsync(cacheKey, progress).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            progress = progress.MarkSubmissionUnknown(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                null,
                "segment.submission.exception-outcome-unknown:" + exception.GetType().Name);
            await PersistUnknownOutcomeAsync(cacheKey, progress).ConfigureAwait(false);
            return (progress, null);
        }
    }

    private async Task PersistUnknownOutcomeAsync(
        ContentAddressedSegmentKey cacheKey,
        GenerationSegmentProgress progress)
    {
        await _progressStore.SaveAsync(progress, CancellationToken.None).ConfigureAwait(false);
        await SetProtectionIfMaterializedAsync(
            cacheKey,
            GenerationCacheLifecycleState.UnresolvedSubmission,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<GenerationScheduledSegmentResult> ApplySubmissionResultAsync(
        GenerationScheduledSegment segment,
        ContentAddressedSegmentKey cacheKey,
        GenerationSegmentProgress progress,
        GenerationSegmentExecutionResult result,
        CancellationToken cancellationToken)
    {
        EnsureCacheIdentity(result.CacheKey, cacheKey, "Executor");
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (result.Disposition == SubmissionDisposition.Accepted)
        {
            progress = progress.MarkCompleted(now, result.CacheKey.PrivateLookupHmacSha256, result.ProviderRequestId, result.DiagnosticCode);
            await SetProtectionIfMaterializedAsync(cacheKey, GenerationCacheLifecycleState.Completed, cancellationToken).ConfigureAwait(false);
        }
        else if (result.RequiresReconciliation)
        {
            progress = progress.MarkSubmissionUnknown(now, result.ProviderRequestId, result.DiagnosticCode);
            await SetProtectionIfMaterializedAsync(cacheKey, GenerationCacheLifecycleState.UnresolvedSubmission, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var decision = _policy.DecideRetry(
                GenerationJobState.Submitting,
                result.Disposition,
                progress.CompletedAttempts,
                result.RetryAfter,
                DeterministicJitterSeed(segment));
            progress = decision.MayRetryAutomatically
                ? progress.MarkRetryWait(now, decision.Delay, result.DiagnosticCode)
                : progress.MarkFailed(now, result.DiagnosticCode);
            await SetProtectionIfMaterializedAsync(cacheKey, GenerationCacheLifecycleState.Completed, cancellationToken).ConfigureAwait(false);
        }

        await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
        return new GenerationScheduledSegmentResult(progress, result);
    }

    private async Task<GenerationScheduledSegmentResult> ReconcileOnlyAsync(
        GenerationScheduledSegment segment,
        GenerationSegmentProgress progress,
        CancellationToken cancellationToken)
    {
        var cacheKey = await _executor.CreatePrivateCacheKeyAsync(segment.Request, cancellationToken).ConfigureAwait(false);
        await SetProtectionIfMaterializedAsync(cacheKey, GenerationCacheLifecycleState.UnresolvedSubmission, cancellationToken).ConfigureAwait(false);

        var result = await _executor.ReconcileAsync(segment.Request, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (result is null || result.RequiresReconciliation)
        {
            progress = progress.MarkSubmissionUnknown(
                now,
                result?.ProviderRequestId ?? progress.ProviderRequestId,
                result?.DiagnosticCode ?? "segment.reconciliation.pending");
            await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
            return new GenerationScheduledSegmentResult(progress, result);
        }

        EnsureCacheIdentity(result.CacheKey, cacheKey, "Reconciliation");
        progress = result.Disposition == SubmissionDisposition.Accepted
            ? progress.MarkCompleted(now, result.CacheKey.PrivateLookupHmacSha256, result.ProviderRequestId, result.DiagnosticCode)
            : progress.MarkFailed(now, result.DiagnosticCode);
        await SetProtectionIfMaterializedAsync(cacheKey, GenerationCacheLifecycleState.Completed, cancellationToken).ConfigureAwait(false);
        await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
        return new GenerationScheduledSegmentResult(progress, result);
    }

    private static void EnsureCacheIdentity(
        ContentAddressedSegmentKey observed,
        ContentAddressedSegmentKey expected,
        string operation)
    {
        if (!string.Equals(
            observed.PrivateLookupHmacSha256,
            expected.PrivateLookupHmacSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{operation} returned a cache identity different from the lifecycle-bound segment identity.");
        }
    }

    private async Task SetProtectionIfMaterializedAsync(
        ContentAddressedSegmentKey key,
        GenerationCacheLifecycleState state,
        CancellationToken cancellationToken)
    {
        if (_cacheLifecycleCoordinator is null)
            return;
        if (!await _cache.ContainsAsync(key, cancellationToken).ConfigureAwait(false))
            return;

        switch (state)
        {
            case GenerationCacheLifecycleState.Active:
                await _cacheLifecycleCoordinator.MarkActiveAsync(key, cancellationToken).ConfigureAwait(false);
                break;
            case GenerationCacheLifecycleState.UnresolvedSubmission:
                await _cacheLifecycleCoordinator.MarkUnresolvedSubmissionAsync(key, cancellationToken).ConfigureAwait(false);
                break;
            case GenerationCacheLifecycleState.Completed:
                await _cacheLifecycleCoordinator.MarkCompletedAsync(key, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Scheduler cannot apply unsupported cache lifecycle state {state}.");
        }
    }

    private static ulong DeterministicJitterSeed(GenerationScheduledSegment segment)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(segment.JobId.ToString("N") + "\n" + segment.SegmentId));
        return BitConverter.ToUInt64(bytes, 0);
    }

    private static void Validate(GenerationScheduledSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ValidateIdentity(segment.JobId, segment.SegmentId, segment.SegmentIndex, segment.Request);
    }

    private static void ValidateIdentity(
        Guid jobId,
        string segmentId,
        int segmentIndex,
        GenerationSegmentExecutionRequest request)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job id is required.", nameof(jobId));
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (segmentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        ArgumentNullException.ThrowIfNull(request);
    }
}

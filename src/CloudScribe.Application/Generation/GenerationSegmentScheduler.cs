using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationScheduledSegment(
    Guid JobId,
    string SegmentId,
    int SegmentIndex,
    GenerationSegmentExecutionRequest Request);

public sealed record GenerationScheduledSegmentResult(
    GenerationSegmentProgress Progress,
    GenerationSegmentExecutionResult? ExecutionResult);

public sealed class GenerationSegmentScheduler
{
    private readonly GenerationSegmentExecutor _executor;
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
        ArgumentNullException.ThrowIfNull(cache);
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
        var progress = await _progressStore.ReadAsync(segment.JobId, segment.SegmentId, cancellationToken).ConfigureAwait(false)
            ?? new GenerationSegmentProgress(
                segment.JobId,
                segment.SegmentId,
                segment.SegmentIndex,
                segment.Request.IdempotencyKey,
                GenerationSegmentProgressState.Pending,
                0,
                now);

        if (!string.Equals(progress.IdempotencyKey, segment.Request.IdempotencyKey, StringComparison.Ordinal) || progress.SegmentIndex != segment.SegmentIndex)
        {
            throw new InvalidOperationException("Persisted segment progress does not match the immutable scheduled segment identity.");
        }

        if (progress.IsTerminal)
        {
            return new GenerationScheduledSegmentResult(progress, null);
        }

        if (progress.RequiresReconciliation)
        {
            return await ReconcileOnlyAsync(segment, progress, cancellationToken).ConfigureAwait(false);
        }

        if (progress.State == GenerationSegmentProgressState.RetryWait &&
            progress.NotBeforeUnixMilliseconds is { } notBefore && now < notBefore)
        {
            return new GenerationScheduledSegmentResult(progress, null);
        }

        // Cache reuse is intentionally owned only by GenerationSegmentExecutor. The executor
        // applies the v2.23 private trust namespace, Force Fresh, structural media validation,
        // and metadata-qualified cache-hit eligibility. A scheduler-level shortcut would bypass
        // those controls and could incorrectly mark an ineligible cache entry completed.
        var cacheKey = await _executor.CreatePrivateCacheKeyAsync(segment.Request, cancellationToken).ConfigureAwait(false);
        if (_cacheLifecycleCoordinator is not null)
            await _cacheLifecycleCoordinator.MarkActiveAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        progress = progress.MarkSubmissionStarted(now);
        await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);

        GenerationSegmentExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(segment.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress = progress.MarkSubmissionUnknown(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                null,
                "segment.submission.cancelled-outcome-unknown");
            await _progressStore.SaveAsync(progress, CancellationToken.None).ConfigureAwait(false);
            if (_cacheLifecycleCoordinator is not null)
                await _cacheLifecycleCoordinator.MarkUnresolvedSubmissionAsync(cacheKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            progress = progress.MarkSubmissionUnknown(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                null,
                "segment.submission.exception-outcome-unknown:" + exception.GetType().Name);
            await _progressStore.SaveAsync(progress, CancellationToken.None).ConfigureAwait(false);
            if (_cacheLifecycleCoordinator is not null)
                await _cacheLifecycleCoordinator.MarkUnresolvedSubmissionAsync(cacheKey, CancellationToken.None).ConfigureAwait(false);
            return new GenerationScheduledSegmentResult(progress, null);
        }

        if (!string.Equals(result.CacheKey.PrivateLookupHmacSha256, cacheKey.PrivateLookupHmacSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Executor returned a cache identity different from the lifecycle-bound segment identity.");

        now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (result.Disposition == SubmissionDisposition.Accepted)
        {
            progress = progress.MarkCompleted(now, result.CacheKey.PrivateLookupHmacSha256, result.ProviderRequestId, result.DiagnosticCode);
            if (_cacheLifecycleCoordinator is not null)
                await _cacheLifecycleCoordinator.MarkCompletedAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
        else if (result.RequiresReconciliation)
        {
            progress = progress.MarkSubmissionUnknown(now, result.ProviderRequestId, result.DiagnosticCode);
            if (_cacheLifecycleCoordinator is not null)
                await _cacheLifecycleCoordinator.MarkUnresolvedSubmissionAsync(cacheKey, cancellationToken).ConfigureAwait(false);
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
            if (_cacheLifecycleCoordinator is not null)
                await _cacheLifecycleCoordinator.MarkCompletedAsync(cacheKey, cancellationToken).ConfigureAwait(false);
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
        if (_cacheLifecycleCoordinator is not null)
            await _cacheLifecycleCoordinator.MarkUnresolvedSubmissionAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        var result = await _executor.ReconcileAsync(segment.Request, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (result is null || result.RequiresReconciliation)
        {
            progress = progress.MarkSubmissionUnknown(now, result?.ProviderRequestId ?? progress.ProviderRequestId, result?.DiagnosticCode ?? "segment.reconciliation.pending");
            await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
            return new GenerationScheduledSegmentResult(progress, result);
        }

        if (!string.Equals(result.CacheKey.PrivateLookupHmacSha256, cacheKey.PrivateLookupHmacSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reconciliation returned a cache identity different from the lifecycle-bound segment identity.");

        if (result.Disposition == SubmissionDisposition.Accepted)
        {
            progress = progress.MarkCompleted(now, result.CacheKey.PrivateLookupHmacSha256, result.ProviderRequestId, result.DiagnosticCode);
        }
        else
        {
            progress = progress.MarkFailed(now, result.DiagnosticCode);
        }

        if (_cacheLifecycleCoordinator is not null)
            await _cacheLifecycleCoordinator.MarkCompletedAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        await _progressStore.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
        return new GenerationScheduledSegmentResult(progress, result);
    }

    private static ulong DeterministicJitterSeed(GenerationScheduledSegment segment)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(segment.JobId.ToString("N") + "\n" + segment.SegmentId));
        return BitConverter.ToUInt64(bytes, 0);
    }

    private static void Validate(GenerationScheduledSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.JobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(segment));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(segment.SegmentId);
        if (segment.SegmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segment));
        }

        ArgumentNullException.ThrowIfNull(segment.Request);
    }
}

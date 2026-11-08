using System.Collections.Concurrent;
using System.Diagnostics;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Batch;

/// <summary>Runs a batch one file at a time. Safety rules, in order of importance:
/// <list type="number">
/// <item>A file whose job identity is recorded is only ever continued or reconciled, never started again.</item>
/// <item>An uncertain file is never resubmitted automatically; the user must ask to check it again.</item>
/// <item>Each state change is saved before the next step, so a crash resumes from the last saved boundary.</item>
/// <item>One run per batch: an in-process gate plus an OS file lock refuse duplicate starts.</item>
/// <item>A failed file never erases completed files; completed outputs are re-verified and reused.</item>
/// </list></summary>
public sealed class BatchQueueRunner(IBatchQueueStore store, IBatchNarrator narrator, TimeProvider clock, ILogger? logger = null)
{
    private static readonly ConcurrentDictionary<Guid, byte> Active = new();
    private volatile bool _pauseRequested;

    /// <summary>Stop starting new files. The current file stops at its next safe boundary; a request already
    /// submitted may still finish at the provider and be charged.</summary>
    public void RequestPause() => _pauseRequested = true;

    public static bool IsRunning(Guid batchId) => Active.ContainsKey(batchId);

    public async Task<NarrationBatch> RunAsync(Guid batchId, string currentSettingsSha256, IProgress<NarrationBatch>? progress = null,
        IProgress<BatchItemProgress>? itemProgress = null, CancellationToken cancellationToken = default)
    {
        EnterRun(batchId);
        try
        {
            using var runLock = store.TryAcquireRunLock(batchId) ?? throw new BatchAlreadyRunningException();
            _pauseRequested = false;
            var queue = await store.LoadAsync(batchId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The batch could not be found.");
            if (queue.State == BatchQueueState.Finished) return queue;
            if (!string.Equals(queue.SettingsSha256, currentSettingsSha256, StringComparison.Ordinal)
                || !string.Equals(queue.Settings.Sha256(), queue.SettingsSha256, StringComparison.Ordinal))
                throw new BatchSettingsChangedException();
            var (authorized, explanation) = await narrator.CheckAuthorityAsync(queue, cancellationToken).ConfigureAwait(false);
            if (!authorized)
            {
                queue = await SaveAsync(queue with { State = BatchQueueState.Paused, ReasonCode = "batch.not-authorized" }, progress, cancellationToken).ConfigureAwait(false);
                throw new BatchNotAuthorizedException(explanation);
            }
            var now = clock.GetUtcNow();
            queue = await SaveAsync(queue with { State = BatchQueueState.Running, ReasonCode = null, FirstStartedUtc = queue.FirstStartedUtc ?? now },
                progress, cancellationToken).ConfigureAwait(false);
            if (logger is not null) CloudScribeLog.Observe(() => CloudScribeLog.BatchRunStarted(logger, batchId, queue.IncludedCount, queue.CompletedCount));
            var runClock = Stopwatch.StartNew();
            string? stopReason = null;
            try
            {
                // Reconcile explicitly selected existing jobs before starting new files. Output/track ordering is unchanged.
                foreach (var itemId in queue.Items.OrderBy(x => x.JobId is not null && x.State is BatchItemState.Ready or BatchItemState.Running ? 0 : 1)
                    .ThenBy(x => x.Order).Select(x => x.ItemId).ToArray())
                {
                    if (_pauseRequested || cancellationToken.IsCancellationRequested) { stopReason = "batch.paused"; break; }
                    var item = queue.Items.Single(x => x.ItemId == itemId);
                    (queue, stopReason) = await ProcessAsync(queue, item, progress, itemProgress, cancellationToken).ConfigureAwait(false);
                    if (stopReason is not null) break;
                }
            }
            finally
            {
                runClock.Stop();
                var latest = await store.LoadAsync(batchId, CancellationToken.None).ConfigureAwait(false) ?? queue;
                bool finished = stopReason is null && latest.Included.All(x => x.IsTerminal);
                queue = await SaveAsync(latest with
                {
                    State = finished ? BatchQueueState.Finished : BatchQueueState.Paused,
                    ReasonCode = finished ? "batch.finished" : stopReason ?? "batch.interrupted",
                    FinishedUtc = finished ? clock.GetUtcNow() : latest.FinishedUtc,
                    ActiveMilliseconds = latest.ActiveMilliseconds + runClock.ElapsedMilliseconds,
                }, progress, CancellationToken.None).ConfigureAwait(false);
                if (logger is not null) LogSummary(logger, queue);
            }
            return queue;
        }
        catch (Exception error) when (BatchDiagnostics.NonFatal(error))
        {
            BatchDiagnostics.Failure(logger, batchId, null, BatchDiagnosticOperation.Run, error);
            throw;
        }
        finally { Active.TryRemove(batchId, out _); }
    }

    private void EnterRun(Guid batchId)
    {
        if (Active.TryAdd(batchId, 0)) return;
        var error = new BatchAlreadyRunningException();
        BatchDiagnostics.Failure(logger, batchId, null, BatchDiagnosticOperation.AcquireRunLock, error);
        throw error;
    }

    private async Task<(NarrationBatch Queue, string? StopReason)> ProcessAsync(NarrationBatch queue, BatchItem item,
        IProgress<NarrationBatch>? progress, IProgress<BatchItemProgress>? itemProgress, CancellationToken token)
    {
        switch (item.State)
        {
            case BatchItemState.Excluded or BatchItemState.Skipped or BatchItemState.Failed or BatchItemState.Uncertain:
                return (queue, null);
            case BatchItemState.Completed:
                return await VerifyCompletedItemAsync(queue, item, progress, token).ConfigureAwait(false);
        }
        if (item.DocumentId is null) throw new InvalidOperationException("A queued file has no imported document.");
        // Ready, or Running after an interruption: a recorded JobId makes the narrator continue/reconcile, never restart.
        item = item with
        {
            State = BatchItemState.Running,
            Attempts = item.Attempts + 1,
            ReasonCode = null,
            StartedUtc = item.StartedUtc ?? clock.GetUtcNow(),
        };
        queue = await SaveAsync(queue.WithItem(item), progress, token).ConfigureAwait(false);
        if (logger is not null) CloudScribeLog.Observe(() => CloudScribeLog.BatchItemStarted(logger, queue.BatchId, item.Order, item.JobId is not null));
        LogJobLink(queue.BatchId, item);
        var clockForItem = Stopwatch.StartNew();
        BatchNarrationOutcome outcome;
        try
        {
            outcome = await narrator.NarrateAsync(queue, item, async jobId =>
            {
                if (item.JobId is { } existing && existing != jobId)
                    throw new InvalidOperationException("The narrator reported a different job for a file that already has one.");
                item = item with { JobId = jobId };
                queue = await SaveAsync(queue.WithItem(item), progress, CancellationToken.None).ConfigureAwait(false);
                LogJobLink(queue.BatchId, item);
            }, itemProgress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _pauseRequested)
        {
            outcome = new(BatchOutcomeKind.Paused, "batch.paused");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            BatchDiagnostics.Failure(logger, queue.BatchId, item.ItemId, BatchDiagnosticOperation.Narrate, error);
            // After a job identity exists, an unexpected failure may hide a submitted request: never assume it was not sent.
            outcome = new(item.JobId is null ? BatchOutcomeKind.Failed : BatchOutcomeKind.Uncertain,
                item.JobId is null ? "batch.narration-failed-before-start" : "batch.narration-failed-after-start-reconcile");
        }
        clockForItem.Stop();
        var updated = Apply(queue.Items.Single(x => x.ItemId == item.ItemId), outcome, clockForItem.ElapsedMilliseconds, clock.GetUtcNow());
        // Success is only declared for a file that is really there with the reported size and hash.
        if (updated.State == BatchItemState.Completed && !await narrator.VerifyOutputAsync(updated, CancellationToken.None).ConfigureAwait(false))
            updated = updated with { State = BatchItemState.Failed, ReasonCode = "batch.output-not-verified", FinishedUtc = null };
        queue = await SaveAsync(queue.WithItem(updated), progress, CancellationToken.None).ConfigureAwait(false);
        LogOutcome(queue.BatchId, updated, clockForItem.ElapsedMilliseconds);
        string? stop = outcome.Kind switch
        {
            BatchOutcomeKind.Paused => "batch.paused",
            BatchOutcomeKind.NotAuthorized => "batch.not-authorized",
            _ when updated.State is BatchItemState.Failed or BatchItemState.Uncertain && queue.Settings.FailurePolicy == BatchFailurePolicy.StopBatch => "batch.stopped-on-failure",
            _ => null,
        };
        return (queue, stop);
    }

    private async Task<(NarrationBatch Queue, string? StopReason)> VerifyCompletedItemAsync(NarrationBatch queue, BatchItem item,
        IProgress<NarrationBatch>? progress, CancellationToken token)
    {
        if (await narrator.VerifyOutputAsync(item, token).ConfigureAwait(false)) return (queue, null);
        queue = await SaveAsync(queue.WithItem(item with { State = BatchItemState.Failed, ReasonCode = "batch.output-missing-or-changed" }),
            progress, token).ConfigureAwait(false);
        if (logger is not null) CloudScribeLog.Observe(() => CloudScribeLog.BatchItemStopped(logger, queue.BatchId, item.Order, BatchItemState.Failed, "batch.output-missing-or-changed"));
        return (queue, queue.Settings.FailurePolicy == BatchFailurePolicy.StopBatch ? "batch.stopped-on-failure" : null);
    }

    private void LogJobLink(Guid batchId, BatchItem item)
    {
        if (logger is not null && item.JobId is { } jobId)
            CloudScribeLog.Observe(() => CloudScribeLog.BatchItemJobLinked(logger, batchId, item.ItemId, item.Order, jobId));
    }

    private void LogOutcome(Guid batchId, BatchItem updated, long elapsedMilliseconds)
    {
        if (logger is null) return;
        if (updated.State == BatchItemState.Completed) CloudScribeLog.Observe(() => CloudScribeLog.BatchItemCompleted(logger, batchId, updated.Order, elapsedMilliseconds, updated.OutputBytes));
        else CloudScribeLog.Observe(() => CloudScribeLog.BatchItemStopped(logger, batchId, updated.Order, updated.State, updated.ReasonCode ?? "none"));
    }

    private static BatchItem Apply(BatchItem latest, BatchNarrationOutcome outcome, long elapsedMilliseconds, DateTimeOffset now)
    {
        var updated = latest with
        {
            ActiveMilliseconds = latest.ActiveMilliseconds + elapsedMilliseconds,
            EstimatedMaximumMinorUnits = outcome.EstimatedMaximumMinorUnits > 0 ? outcome.EstimatedMaximumMinorUnits : latest.EstimatedMaximumMinorUnits,
            ReservedMinorUnits = Math.Max(latest.ReservedMinorUnits, outcome.ReservedMinorUnits),
            ReasonCode = outcome.ReasonCode,
            UncertainOutcomes = outcome.Kind == BatchOutcomeKind.Uncertain ? latest.UncertainOutcomes + 1 : latest.UncertainOutcomes,
        };
        return outcome.Kind switch
        {
            BatchOutcomeKind.Completed => updated with
            {
                State = BatchItemState.Completed,
                OutputPath = outcome.OutputPath,
                OutputBytes = outcome.OutputBytes,
                OutputSha256 = outcome.OutputSha256,
                FinishedUtc = now,
            },
            BatchOutcomeKind.Failed => updated with { State = BatchItemState.Failed },
            BatchOutcomeKind.Uncertain => updated with { State = BatchItemState.Uncertain },
            // Paused or not authorized: the file goes back to Ready, keeping any JobId so the next run reconciles it.
            _ => updated with { State = BatchItemState.Ready },
        };
    }

    private async Task<NarrationBatch> SaveAsync(NarrationBatch queue, IProgress<NarrationBatch>? progress, CancellationToken token)
    {
        var saved = await store.SaveAsync(queue, token).ConfigureAwait(false);
        progress?.Report(saved);
        return saved;
    }

    /// <summary>Lets a failed file run again. Its saved job (if any) is continued, so accepted audio is reused.</summary>
    public static NarrationBatch Retry(NarrationBatch queue, Guid itemId) => Transition(queue, itemId, BatchItemState.Failed, BatchItemState.Ready);

    /// <summary>Asks the narrator to check an uncertain file's saved job again. This reconciles; it does not resubmit.</summary>
    public static NarrationBatch CheckAgain(NarrationBatch queue, Guid itemId) => Transition(queue, itemId, BatchItemState.Uncertain, BatchItemState.Ready);

    public static NarrationBatch Skip(NarrationBatch queue, Guid itemId)
    {
        var item = queue.Items.Single(x => x.ItemId == itemId);
        if (item.State is BatchItemState.Running or BatchItemState.Completed) return queue;
        return queue.WithItem(item with { State = BatchItemState.Skipped }) with { State = queue.State == BatchQueueState.Finished ? BatchQueueState.Paused : queue.State };
    }

    private static NarrationBatch Transition(NarrationBatch queue, Guid itemId, BatchItemState from, BatchItemState to)
    {
        var item = queue.Items.Single(x => x.ItemId == itemId);
        if (item.State != from) return queue;
        return queue.WithItem(item with { State = to, ReasonCode = null }) with
        {
            State = queue.State == BatchQueueState.Finished ? BatchQueueState.Paused : queue.State,
            FinishedUtc = null,
        };
    }

    private static void LogSummary(ILogger logger, NarrationBatch queue)
    {
        if (!CloudScribeLog.ObserveIsEnabled(logger, LogLevel.Information)) return;
        int failed = queue.Items.Count(x => x.State == BatchItemState.Failed);
        int uncertain = queue.Items.Count(x => x.State == BatchItemState.Uncertain);
        int completed = queue.CompletedCount, included = queue.IncludedCount;
        long estimated = queue.EstimatedMaximumMinorUnits, reserved = queue.ReservedMinorUnits;
        CloudScribeLog.Observe(() => CloudScribeLog.BatchCostDenomination(logger, queue.BatchId, queue.Currency, queue.CurrencyScale));
        CloudScribeLog.Observe(() => CloudScribeLog.BatchRunSummary(logger, queue.BatchId, queue.State, completed, failed, uncertain,
            included, queue.ActiveMilliseconds, estimated, reserved, queue.ReasonCode ?? "none"));
    }
}

using System.Security.Cryptography;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Narrates one queued file with the complete-article workflow. Before anything is reserved it checks that the
/// service's own exact review matches the approved plan and stays within the file's approved maximum. A file that
/// already has a job is only continued through the same reviewed job; a new job is never created for it.</summary>
public sealed class CompleteArticleBatchNarrator(BatchAuthorityGate gate, IBatchArticleWorkflow workflow,
    Func<(string AccountStableId, string ProjectStableId)> identity, ILogger? logger = null) : IBatchNarrator
{
    public async Task<(bool Authorized, string Explanation)> CheckAuthorityAsync(NarrationBatch queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (!string.Equals(queue.Settings.Engine, BatchEngines.CompleteArticle, StringComparison.Ordinal))
            return (false, "This batch uses an engine that is not available for batches in this version.");
        var (account, project) = identity();
        var decision = await gate.CheckRunAsync(queue, account, project, cancellationToken).ConfigureAwait(false);
        return (decision.Allowed, decision.Explanation);
    }

    public async Task<BatchNarrationOutcome> NarrateAsync(NarrationBatch queue, BatchItem item, Func<Guid, Task> recordJob,
        IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(recordJob);
        var (account, project) = identity();
        var decision = await gate.CheckItemAsync(queue, item, account, project, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed) return new(BatchOutcomeKind.NotAuthorized, decision.ReasonCode);
        var consent = await gate.LoadConsentAsync(queue.BatchId, cancellationToken).ConfigureAwait(false);
        var approved = consent?.Items.SingleOrDefault(x => x.ItemId == item.ItemId);
        if (approved is null || item.DocumentId is not { } documentId || approved.DocumentId != documentId)
            return new(BatchOutcomeKind.NotAuthorized, "batch.file-not-approved");
        string destination = item.OutputPath ?? Path.Combine(queue.OutputFolder, item.OutputFileName);
        if (item.JobId is null && await CheckOutputBeforeStartAsync(queue, item, destination, approved, cancellationToken).ConfigureAwait(false) is { } blocked)
            return blocked;
        ChirpWholeArticleJob job;
        var existing = item.JobId is { } saved ? await workflow.ReadAsync(saved, cancellationToken).ConfigureAwait(false) : null;
        if (existing is not null && existing.State != ChirpWholeArticleState.Stopped)
            job = existing; // Ready, Executing, Assembling or Complete: the service itself reconciles before advancing.
        else
        {
            // New file, or a stopped job the user chose to retry or check again: the service's own review decides whether
            // the same job may continue (it refuses when a request still needs reconciliation).
            progress?.Report(new(item.ItemId, "Reviewing the exact plan and maximum", 0, 0));
            ChirpWholeArticleReview review;
            try { review = await workflow.PrepareAsync(documentId, cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException error)
            {
                BatchDiagnostics.Failure(logger, queue.BatchId, item.ItemId, BatchDiagnosticOperation.Review, error);
                return existing is null
                    ? new(BatchOutcomeKind.Failed, "batch.article-review-refused-nothing-sent", EstimatedMaximumMinorUnits: approved.MaximumMinorUnits)
                    : new(BatchOutcomeKind.Uncertain, "batch.article-needs-reconciliation", EstimatedMaximumMinorUnits: approved.MaximumMinorUnits);
            }
            if (!string.Equals(review.Currency, consent!.Currency, StringComparison.Ordinal) || review.Scale != consent.Scale)
                return Stop(existing, "batch.currency-mismatch-nothing-sent", approved);
            if (!string.Equals(review.PlanSha256, approved.PlanSha256, StringComparison.Ordinal))
                return Stop(existing, "batch.plan-changed-nothing-sent", approved);
            if (review.MaximumAdditionalMinorUnits > approved.MaximumMinorUnits)
                return Stop(existing, "batch.file-over-approved-maximum-nothing-sent", approved);
            job = await workflow.StartAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true),
                cancellationToken).ConfigureAwait(false);
        }
        // Recorded before any paid request; the runner refuses a job identity that differs from one already saved.
        if (item.JobId != job.JobId) await recordJob(job.JobId).ConfigureAwait(false);
        ChirpWholeArticleJob result;
        try { result = await workflow.RunAsync(job.JobId, queue, item, destination, progress, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason == LocalRequestReason.ArticleOutputPreflight)
        {
            BatchDiagnostics.Failure(logger, queue.BatchId, item.ItemId, BatchDiagnosticOperation.VerifyOutput, error);
            return new(BatchOutcomeKind.Uncertain, "batch.article-output-preflight-reconcile", EstimatedMaximumMinorUnits: approved.MaximumMinorUnits,
                ReservedMinorUnits: Math.Max(job.MaximumAdditionalMinorUnits, approved.MaximumMinorUnits));
        }
        return BatchArticleOutcomes.FromJob(result, approved.MaximumMinorUnits, cancellationToken.IsCancellationRequested);
    }

    private async Task<BatchNarrationOutcome?> CheckOutputBeforeStartAsync(NarrationBatch queue, BatchItem item,
        string destination, BatchConsentItem approved, CancellationToken token)
    {
        try { await workflow.PreflightAsync(destination, token).ConfigureAwait(false); return null; }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            BatchDiagnostics.Failure(logger, queue.BatchId, item.ItemId, BatchDiagnosticOperation.VerifyOutput, error);
            return new(BatchOutcomeKind.Failed, "batch.article-output-preflight-nothing-sent", EstimatedMaximumMinorUnits: approved.MaximumMinorUnits);
        }
    }

    public async Task<bool> VerifyOutputAsync(BatchItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.OutputPath is null || item.OutputSha256 is null || !File.Exists(item.OutputPath)) return false;
        try
        {
            var stream = File.OpenRead(item.OutputPath);
            await using var owned = stream.ConfigureAwait(false);
            if (stream.Length != item.OutputBytes) return false;
            string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return string.Equals(sha, item.OutputSha256, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            BatchDiagnostics.Failure(logger, null, item.ItemId, BatchDiagnosticOperation.VerifyOutput, error);
            return false;
        }
    }

    // Nothing was reserved for this attempt. A file that already had a job keeps it for reconciliation.
    private static BatchNarrationOutcome Stop(ChirpWholeArticleJob? existing, string reason, BatchConsentItem approved) =>
        new(existing is null ? BatchOutcomeKind.Failed : BatchOutcomeKind.Uncertain, reason, EstimatedMaximumMinorUnits: approved.MaximumMinorUnits);
}

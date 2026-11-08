using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Narrates one file with Long Audio. Before anything can be submitted it checks that the service's own review
/// is exactly the approved request (attempt, binding) and within its approved ceiling, and records the attempt as the
/// file's job. A file whose operation needs reconciliation is never resubmitted. A verified MP3 that is already saved is
/// reused: a failed local copy is retried without new speech.</summary>
public sealed class LongAudioBatchNarrator(BatchAuthorityGate gate, IBatchLongAudioWorkflow workflow, ILogger? logger = null) : IBatchNarrator
{
    public Task<(bool Authorized, string Explanation)> CheckAuthorityAsync(NarrationBatch queue, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the batch router for authority.");

    public async Task<BatchNarrationOutcome> NarrateAsync(NarrationBatch queue, BatchItem item, Func<Guid, Task> recordJob,
        IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(recordJob);
        var consent = await gate.LoadConsentAsync(queue.BatchId, cancellationToken).ConfigureAwait(false);
        var approved = consent?.Items.SingleOrDefault(x => x.ItemId == item.ItemId);
        if (approved?.LongAudio is not { } la || item.DocumentId is not { } documentId || approved.DocumentId != documentId)
            return new(BatchOutcomeKind.NotAuthorized, "batch.file-not-approved");
        bool started = item.JobId is not null;
        progress?.Report(new(item.ItemId, "Checking the exact Long Audio request and its cost", 0, 0));
        GoogleLongAudioDocumentReview review;
        try { review = await workflow.ReviewAsync(documentId, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or GoogleLongAudioBudgetExceededException)
        {
            BatchDiagnostics.Failure(logger, queue.BatchId, item.ItemId, BatchDiagnosticOperation.Review, error);
            return started
                ? new(BatchOutcomeKind.Uncertain, "batch.long-audio-needs-reconciliation", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents)
                : new(BatchOutcomeKind.Failed, "batch.long-audio-not-admitted-nothing-sent", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents);
        }
        if (review.Request.AttemptId != la.AttemptId || !string.Equals(review.Request.BindingSha256, la.RequestBindingSha256, StringComparison.Ordinal))
            return new(started ? BatchOutcomeKind.Uncertain : BatchOutcomeKind.Failed, "batch.plan-changed-nothing-sent", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents);
        if (!review.Cost.IsAlreadyCommitted && review.Cost.EstimatedSynthesisUsdCents > la.CeilingUsdCents)
            return new(started ? BatchOutcomeKind.Uncertain : BatchOutcomeKind.Failed, "batch.file-over-approved-maximum-nothing-sent", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents);
        // The attempt is the file's job identity; recorded before anything can be submitted.
        if (item.JobId != la.AttemptId) await recordJob(la.AttemptId).ConfigureAwait(false);
        var output = await workflow.InspectOutputAsync(review, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            if (await workflow.ContinuationAsync(review, cancellationToken).ConfigureAwait(false) == GoogleLongAudioContinuationKind.ReconcileRequired)
                return new(BatchOutcomeKind.Uncertain, "batch.long-audio-needs-reconciliation", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents,
                    ReservedMinorUnits: await HeldAsync(review, la, cancellationToken).ConfigureAwait(false));
            var run = await workflow.RunAsync(review, progress, item.ItemId, cancellationToken).ConfigureAwait(false);
            long heldNow = await HeldAsync(review, la, CancellationToken.None).ConfigureAwait(false);
            if (run.Stage != Application.Generation.LongAudioWorkflowStage.Completed)
                return new(BatchOutcomeKind.Uncertain, run.Stage == Application.Generation.LongAudioWorkflowStage.Stopped ? "batch.long-audio-stopped-check-again" : "batch.long-audio-failed-check-again",
                    EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents, ReservedMinorUnits: heldNow);
            output = await workflow.InspectOutputAsync(review, CancellationToken.None).ConfigureAwait(false);
            if (output is null)
                return new(BatchOutcomeKind.Uncertain, "batch.long-audio-output-not-verified", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents, ReservedMinorUnits: heldNow);
        }
        var audio = output.Value;
        long held = await HeldAsync(review, la, CancellationToken.None).ConfigureAwait(false);
        if (!audio.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            return new(BatchOutcomeKind.Failed, "batch.long-audio-output-not-mp3-speech-kept", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents, ReservedMinorUnits: held);
        string destination = item.OutputPath ?? Path.Combine(queue.OutputFolder, item.OutputFileName);
        progress?.Report(new(item.ItemId, "Copying the verified MP3 to the output folder", 0, 0));
        try { await BatchOutputFiles.CopyVerifiedAsync(audio.Path, audio.Sha256, audio.Bytes, destination, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BatchDiagnostics.Failure(logger, queue.BatchId, item.ItemId, BatchDiagnosticOperation.VerifyOutput, error);
            // Speech is saved and verified in the profile; Retry copies it again without any new request.
            return new(BatchOutcomeKind.Failed, "batch.local-copy-failed-speech-kept", EstimatedMaximumMinorUnits: la.FamilyMaximumUsdCents, ReservedMinorUnits: held);
        }
        return BatchNarrationOutcome.Completed(destination, audio.Bytes, audio.Sha256, la.FamilyMaximumUsdCents, held);
    }

    public Task<bool> VerifyOutputAsync(BatchItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return BatchOutputFiles.MatchesAsync(item.OutputPath, item.OutputSha256, item.OutputBytes, cancellationToken);
    }

    private async Task<long> HeldAsync(GoogleLongAudioDocumentReview review, BatchLongAudioApproval la, CancellationToken token)
    {
        try { return await workflow.CommittedUsdCentsAsync(review, token).ConfigureAwait(false) ?? la.FamilyMaximumUsdCents; }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException) { return la.FamilyMaximumUsdCents; }
    }
}

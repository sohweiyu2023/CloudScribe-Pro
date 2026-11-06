using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioDocumentService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "A single serialized operation owns polling, stop-after-outcome and the original failure; no paid retry loop exists.")]
    public async Task<GoogleLongAudioDocumentRun> RunAsync(GoogleLongAudioDocumentReview review, GoogleLongAudioMediaTools tools,
        bool captureConfirmed, GoogleLongAudioRunControl control, IProgress<GoogleLongAudioDocumentProgress>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!logger.IsEnabled(LogLevel.Information)) throw new InvalidOperationException("Required Long Audio structural evidence is disabled. No request was submitted.");
        long started = clock.GetTimestamp(); GoogleLongAudioCheckpoint? latest = null;
        try
        {
            if (review.AdaptiveRecoveryEnabled)
                return await RunRecoveryAsync(review, tools, captureConfirmed, control, progress, token).ConfigureAwait(false);
            trendHistory.RequireReady(); GoogleLongAudioStructure.Log(logger, review.Request); trendHistory.RequireReady();
            Observe(LongAudioWorkflowStage.LocalSetup, null);
            using var session = await OpenAsync(review, tools, captureConfirmed, token).ConfigureAwait(false);
            if (control.IsStopRequested) throw new OperationCanceledException("Long Audio stopped before submission.", token);
            Observe(LongAudioWorkflowStage.SubmittingOrResuming, null);
            var outcome = await session.AdvanceAsync(token).ConfigureAwait(false);
            while (true)
            {
                latest = outcome.Checkpoint;
                if (outcome.Artifact is not null)
                {
                    await SaveReceiptAsync(review, outcome.Artifact, token).ConfigureAwait(false);
                    Observe(LongAudioWorkflowStage.Completed, latest); return new(LongAudioWorkflowStage.Completed, outcome);
                }
                if (latest.State != GoogleLongAudioState.Pollable || latest.HttpStatus is < 200 or >= 300 || latest.Observation is not { ReadbackVerified: true })
                { Observe(LongAudioWorkflowStage.Failed, latest); return new(LongAudioWorkflowStage.Failed, outcome); }
                Observe(LongAudioWorkflowStage.WaitingForGoogle, latest);
                await control.WaitBeforePollAsync(clock, token).ConfigureAwait(false);
                if (control.IsStopRequested)
                { Observe(LongAudioWorkflowStage.Stopped, latest); return new(LongAudioWorkflowStage.Stopped, outcome); }
                Observe(LongAudioWorkflowStage.CheckingGoogleAndOutput, latest);
                outcome = await session.PollAsync(token).ConfigureAwait(false); // GET only, never an automatic paid retry.
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            try { Observe(error is OperationCanceledException ? LongAudioWorkflowStage.Stopped : LongAudioWorkflowStage.Failed, latest, error.HResult); }
            catch (Exception evidenceError) when (evidenceError is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { throw new AggregateException("Long Audio and its safe local evidence both failed; reconcile the saved operation.", error, evidenceError); }
            throw;
        }

        void Observe(LongAudioWorkflowStage stage, GoogleLongAudioCheckpoint? state, int? localError = null)
        {
            var capture = state?.Observation;
            long duration = checked((long)clock.GetElapsedTime(started).TotalMilliseconds);
            CloudScribeLog.LongAudioWorkflow(logger, review.Request.AttemptId, review.DocumentId, review.RevisionId,
                review.Request.PayloadSha256, stage, state is null ? null : (int)state.State, state?.HttpStatus, state?.RpcCode,
                duration, capture?.PrivateRecordId, capture?.ReadbackVerified,
                capture?.Truncated, capture?.DetailsOmitted, capture?.ObservedBytes, localError);
            trendHistory.RequireReady(); progress?.Report(new(stage, state));
        }
    }
}

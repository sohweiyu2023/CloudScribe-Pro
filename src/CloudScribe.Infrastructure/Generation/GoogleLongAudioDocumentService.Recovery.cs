using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioDocumentService
{
    private async Task<GoogleLongAudioDocumentRun> RunRecoveryAsync(GoogleLongAudioDocumentReview review, GoogleLongAudioMediaTools tools,
        bool captureConfirmed, GoogleLongAudioRunControl control, IProgress<GoogleLongAudioDocumentProgress>? progress, CancellationToken token)
    {
        if (articleNative is null || !logger.IsEnabled(LogLevel.Information))
            throw new InvalidOperationException("Article assembly and structural logging must be configured before submission.");
        var current = await ReviewAsync(review.DocumentId, token).ConfigureAwait(false);
        if (!current.AdaptiveRecoveryEnabled || !string.Equals(current.ScopeSha256, review.ScopeSha256, StringComparison.Ordinal)
            || !string.Equals(current.Request.BindingSha256, review.Request.BindingSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed article source or recovery scope changed; no request was sent.");
        var context = await RequireOutputContextAsync(current, token).ConfigureAwait(false);
        var journal = new GoogleLongAudioFileJournal(context.OperationsDirectory);
        long started = clock.GetTimestamp();
        void Observe(LongAudioWorkflowStage stage, GoogleLongAudioCheckpoint? checkpoint)
        {
            trendHistory.RequireReady();
            if (!logger.IsEnabled(LogLevel.Information)) throw new InvalidOperationException("Required article logging became unavailable.");
            long duration = checked((long)clock.GetElapsedTime(started).TotalMilliseconds);
            CloudScribeLog.LongAudioWorkflow(logger, current.Request.AttemptId, current.DocumentId, current.RevisionId, current.Request.PayloadSha256,
                stage, checkpoint is null ? null : (int)checkpoint.State, checkpoint?.HttpStatus, checkpoint?.RpcCode,
                duration, checkpoint?.Observation?.PrivateRecordId,
                checkpoint?.Observation?.ReadbackVerified, checkpoint?.Observation?.Truncated, checkpoint?.Observation?.DetailsOmitted,
                checkpoint?.Observation?.ObservedBytes, null);
            trendHistory.RequireReady(); progress?.Report(new(stage, checkpoint));
        }
        Observe(LongAudioWorkflowStage.LocalSetup, null);
        if (control.IsStopRequested) throw new OperationCanceledException("Article stopped before submission.", token);
        var saved = await GoogleLongAudioArticleAssembler.InspectAsync(current, context, token).ConfigureAwait(false);
        if (saved is not null)
        {
            var rootState = journal.Load(current.Request.AttemptId); Observe(LongAudioWorkflowStage.Completed, rootState);
            return new(LongAudioWorkflowStage.Completed, new(rootState, null), saved);
        }
        var runner = await runtime.CreateRecoveryRunnerAsync(current.Request, tools, current.OutputAllowance, captureConfirmed, trendHistory, token).ConfigureAwait(false);
        Observe(LongAudioWorkflowStage.SubmittingOrResuming, null);
        var run = await runner.RunAsync(current.Request, control, progress, token).ConfigureAwait(false);
        var state = run.StoppedOutcome ?? journal.Load(current.Request.AttemptId);
        if (!run.CoverageComplete || control.IsStopRequested)
        {
            var stage = control.IsStopRequested ? LongAudioWorkflowStage.Stopped
                : current.DeferredUnknownRanges?.Any(x => x.AttemptId == state.AttemptId) == true
                    ? LongAudioWorkflowStage.DeferredGap : LongAudioWorkflowStage.Failed;
            Observe(stage, state); return new(stage, new(state, null));
        }
        Observe(LongAudioWorkflowStage.JoiningArticle, state);
        var assembled = await new GoogleLongAudioArticleAssembler(articleNative).AssembleAsync(current, run.Outputs, context, tools, token).ConfigureAwait(false);
        Observe(LongAudioWorkflowStage.VerifyingArticle, state);
        _ = await RequireOutputContextAsync(current, token).ConfigureAwait(false);
        Observe(LongAudioWorkflowStage.Completed, state);
        return new(LongAudioWorkflowStage.Completed, new(state, null), assembled);
    }

    public async Task<GoogleLongAudioArticleOutput?> InspectArticleOutputAsync(GoogleLongAudioDocumentReview review, CancellationToken token = default) =>
        await GoogleLongAudioArticleAssembler.InspectAsync(review, await RequireOutputContextAsync(review, token).ConfigureAwait(false), token).ConfigureAwait(false);
}

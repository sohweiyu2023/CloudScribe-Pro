using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioDocumentService
{
    public async Task<GoogleLongAudioRunSummary> InspectRunSummaryAsync(GoogleLongAudioDocumentReview review, CancellationToken token = default)
    {
        var context = await RequireOutputContextAsync(review, token).ConfigureAwait(false);
        var ledger = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetLedger>(await ChirpPolicyFiles.ReadAsync(context.LedgerPath, 8 * 1024 * 1024, token).ConfigureAwait(false));
        if (ledger.Version != 1 || !string.Equals(ledger.ScopeSha256, context.ScopeSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Spending summary does not match the installed scope.");
        var parentBytes = await ChirpPolicyFiles.ReadAsync(context.ParentPolicyPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        if (!string.Equals(ChirpPolicyFiles.Hash(parentBytes), context.ParentPolicySha256, StringComparison.Ordinal))
            throw new InvalidDataException("Parent provision changed during summary read.");
        var parent = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(parentBytes);
        var timing = await TimingStore(context, review).ReadAsync(token).ConfigureAwait(false);
        return GoogleLongAudioRunSummary.FromLedger(ledger, review.Request.BindingSha256, timing,
            parent.Budget.ProjectCeilingMinorUnits, context.Scope.TaxFxProvisionUsdCents, context.Scope.TotalGoogleCapUsdCents);
    }

    public async Task<GoogleLongAudioDocumentRun> RunAsync(GoogleLongAudioDocumentReview review, GoogleLongAudioMediaTools tools,
        bool captureConfirmed, GoogleLongAudioRunControl control, IProgress<GoogleLongAudioDocumentProgress>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        var context = await RequireOutputContextAsync(review, token).ConfigureAwait(false);
        var store = TimingStore(context, review);
        GoogleLongAudioTiming? started = null;
        var stage = LongAudioWorkflowStage.LocalSetup;
        void Report(Exception error) => CloudScribeLog.LongAudioRunSummaryUnavailable(logger, review.Request.AttemptId, stage, error.HResult);
        bool metricsReady = await GoogleLongAudioRunMetrics.TryRecordAsync(async () =>
        {
            PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(context.OutputDirectory, "Timing requires a physical output directory.");
            started = await store.BeginAsync(clock.GetUtcNow(), review.Cost.IsAlreadyCommitted, token).ConfigureAwait(false);
        }, Report).ConfigureAwait(false);
        long stamp = clock.GetTimestamp();
        stage = LongAudioWorkflowStage.Failed;
        GoogleLongAudioDocumentRun? result = null;
        try
        {
            result = await RunCoreAsync(review, tools, captureConfirmed, control, progress, token).ConfigureAwait(false);
            stage = result.Stage;
        }
        catch (OperationCanceledException) { stage = LongAudioWorkflowStage.Stopped; throw; }
        finally
        {
            if (metricsReady && started is not null)
                metricsReady = await GoogleLongAudioRunMetrics.TryRecordAsync(async () =>
                {
                    var timing = await store.EndAsync(started, clock.GetUtcNow(), clock.GetElapsedTime(stamp), stage, CancellationToken.None).ConfigureAwait(false);
                    var summary = await InspectRunSummaryAsync(review, CancellationToken.None).ConfigureAwait(false);
                    long elapsedMilliseconds = (long)timing.ElapsedAt(clock.GetUtcNow()).TotalMilliseconds;
                    if (logger.IsEnabled(LogLevel.Information)) CloudScribeLog.LongAudioRunSummary(logger, review.Request.AttemptId, stage,
                        elapsedMilliseconds, timing.RecordedActiveMilliseconds,
                        timing.EarlierHistoryUnknown, timing.ActiveTimeIncomplete, summary.RetainedAttempts,
                        summary.JobRetainedCommitmentsUsdCents, summary.AllJobsRetainedCommitmentsUsdCents,
                        summary.CumulativeProvisionUsdCents, summary.ApprovedCapUsdCents);
                }, Report).ConfigureAwait(false);
        }
        return result! with { SummaryUnavailable = !metricsReady };
    }
    private static GoogleLongAudioTimingStore TimingStore(InstalledGoogleLongAudioContext context, GoogleLongAudioDocumentReview review) =>
        new(Path.Combine(context.OutputDirectory, review.Request.AttemptId.ToString("N") + ".timing.json"), review.Request.BindingSha256);
}

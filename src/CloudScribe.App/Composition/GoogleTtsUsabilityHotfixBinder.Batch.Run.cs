using System.Diagnostics;
using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    /// <summary>The request builder with both engines: Long Audio (installed scope plus batch approvals) and the ordinary
    /// engine's exact review for files it has already admitted. Building it sends nothing and commits nothing.</summary>
    private BatchAdmissionRequestBuilder BatchRequestBuilder(ShellViewModel.GoogleTtsStudioRequestSelection voice)
    {
        var planner = longAudioInstalled is not null && batchLongAudioExtension is not null ? new BatchLongAudioPlanner(longAudioInstalled, batchLongAudioExtension) : null;
        var articles = chirpArticles is not null && documentLibrary is not null
            ? new BatchArticleWorkflow(chirpArticles, documentLibrary, (_, _, _) => throw new InvalidOperationException("Review only."), voice, null)
            : null;
        return new BatchAdmissionRequestBuilder(documentLibrary!, TimeProvider.System, planner, articles, narrationCopies);
    }

    /// <summary>Shows each not-yet-started file's reviewed engine and maximum in the saved batch.</summary>
    private static async Task<NarrationBatch> SaveReviewedEnginesAsync(FileBatchQueueStore store, NarrationBatch batch, BatchAdmissionRequest request)
    {
        bool changed = false;
        var items = batch.Items.Select(x =>
        {
            if (x.JobId is not null || request.Items.SingleOrDefault(r => r.ItemId == x.ItemId) is not { } reviewed) return x;
            if (string.Equals(x.Engine, reviewed.Engine, StringComparison.Ordinal) && x.EstimatedMaximumMinorUnits == reviewed.EstimatedMaximumMinorUnits) return x;
            changed = true;
            return x with { Engine = reviewed.Engine, EstimatedMaximumMinorUnits = reviewed.EstimatedMaximumMinorUnits };
        }).ToArray();
        return changed ? await store.SaveAsync(batch with { Items = items }).ConfigureAwait(true) : batch;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One awaited run owns its progress, tools and lifetime; queue safety rules are in the tested BatchQueueRunner.")]
    private async Task RunBatchAsync(Window dialog, ShellViewModel model, BatchSession session, BatchControls ui, Action refresh)
    {
        if (session.Batch is not { } batch || documentLibrary is null) return;
        using var run = new CancellationTokenSource(); // A fresh stop signal for every run, so Resume works after Stop now.
        session.Run = run;
        var status = ui.Status;
        BatchDiagnostics.Action(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, BatchDiagnosticPhase.Requested);
        try
        {
            if (model.TryCaptureBatchVoice(out string? problem) is not { } voice) { status.Text = problem ?? string.Empty; return; }
            var tools = await ChooseExistingMediaToolsAsync(dialog, status).ConfigureAwait(true);
            if (tools is null) { status.Text = "Choose the audio tools (ffmpeg.exe with ffprobe.exe) before starting. Nothing was sent."; return; }
            // Names for files that have not started are re-checked now, so a file created meanwhile is never overwritten.
            batch = await session.Store.SaveAsync(batch with
            {
                Items = BatchPlanner.AssignOutputNames(batch.Items, t => t + ".mp3", n => File.Exists(Path.Combine(batch.OutputFolder, n)), batch.NumberTracks),
            }).ConfigureAwait(true);
            session.Batch = batch;
            var engines = new Dictionary<string, IBatchNarrator>(StringComparer.Ordinal);
            if (chirpArticles is not null && chirpPreparation is not null && wholeArticleRequests is not null && wholeArticleResolver is not null)
            {
                var workflow = new BatchArticleWorkflow(chirpArticles, documentLibrary,
                    (selection, destination, observe) => new ChirpWholeArticleRuntime(chirpPreparation, chirpArticles, wholeArticleRequests, wholeArticleResolver,
                        articleAudio, selection, destination, tools.EncoderPath, () => !model.HasCurrentVoiceLanguageCatalog,
                        token => RefreshBatchSelectionOnUiAsync(model, selection, token), null, catalogLogger, observe, trendHistory, approvedMediaTools: tools),
                    voice, tools);
                engines[BatchEngines.CompleteArticle] = new CompleteArticleBatchNarrator(session.Gate, workflow, () => (voice.AccountStableId, voice.ProjectStableId), catalogLogger);
            }
            if (longAudioDocuments is not null)
            {
                string probe = Path.Combine(Path.GetDirectoryName(tools.EncoderPath)!, "ffprobe.exe");
                var longAudioTools = new GoogleLongAudioMediaTools(tools.EncoderPath, tools.EncoderSha256, probe, tools.ProbeSha256);
                engines[BatchEngines.LongAudio] = new LongAudioBatchNarrator(session.Gate,
                    new BatchLongAudioWorkflow(longAudioDocuments, longAudioTools, ui.CaptureConsent.IsChecked == true), catalogLogger);
            }
            var router = new RoutingBatchNarrator(session.Gate, () => (voice.AccountStableId, voice.ProjectStableId), engines);
            session.Runner = new BatchQueueRunner(session.Store, router, TimeProvider.System, catalogLogger);
            string currentSettings = (batch.Settings with { LanguageCode = voice.LanguageCode, VoiceStableId = voice.VoiceStableId }).Sha256();
            var progress = new Progress<NarrationBatch>(b => { session.Batch = b; refresh(); });
            var itemProgress = new Progress<BatchItemProgress>(p =>
            {
                session.Current = p;
                var item = session.Batch?.Items.FirstOrDefault(x => x.ItemId == p.ItemId);
                ui.Current.Text = item is null ? p.Stage : $"File {item.Order}: {item.SourceFileName} — {p.Stage}"
                    + (p.TotalParts > 0 ? $" ({p.SavedParts:N0}/{p.TotalParts:N0} verified)" : string.Empty);
            });
            session.RunClock.Restart();
            status.Text = "Running. Files are narrated one at a time; finished files are kept even if a later file fails.";
            await model.RunBatchOperationAsync(async token =>
                session.Batch = await session.Runner.RunAsync(batch.BatchId, currentSettings, progress, itemProgress, token).ConfigureAwait(true),
                run.Token).ConfigureAwait(true);
            status.Text = session.Batch?.State == BatchQueueState.Finished
                ? "Batch finished. Each finished file was verified in the output folder."
                : "Batch paused. Choose Resume to continue; finished files are kept and nothing finished is sent again.";
            if (session.Batch is { State: BatchQueueState.Finished, CompletedCount: > 0 } done)
            {
                try
                {
                    BatchManifestWriter.Write(done);
                    BatchDiagnostics.Action(catalogLogger, done.BatchId, null, BatchDiagnosticOperation.WriteManifest, BatchDiagnosticPhase.Returned);
                    status.Text += " A playlist and summary were written to the output folder.";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    BatchDiagnostics.Failure(catalogLogger, done.BatchId, null, BatchDiagnosticOperation.WriteManifest, error);
                    status.Text += " The playlist could not be written.";
                }
            }
        }
        catch (BatchSettingsChangedException error) { BatchDiagnostics.Failure(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, error); status.Text = $"Select the batch's voice ({batch.Settings.VoiceLabel}) again to continue. Nothing was sent."; }
        catch (BatchNotAuthorizedException error) { BatchDiagnostics.Failure(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, error); status.Text = error.Message; }
        catch (BatchAlreadyRunningException error) { BatchDiagnostics.Failure(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, error); status.Text = "This batch is already running in another window."; }
        catch (OperationCanceledException error) { BatchDiagnostics.Failure(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, error); status.Text = "Stopped. Choose Resume to continue; anything already sent is checked, never resent."; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { BatchDiagnostics.Failure(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, error); status.Text = "The batch stopped: " + AudioOutputPresentation.RecoveryAdvice(error) + " Finished files are kept; files that may have been sent are marked Needs checking."; }
        finally
        {
            session.RunClock.Stop();
            session.Current = null; ui.Current.Text = string.Empty;
            session.Run = null;
            try { if (await session.Store.LoadAsync(batch.BatchId).ConfigureAwait(true) is { } latest) session.Batch = latest; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                BatchDiagnostics.Failure(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.LoadSaved, error);
                status.Text += " The saved batch could not be re-read; reopen it.";
            }
            session.Active = null; refresh();
            BatchDiagnostics.Action(catalogLogger, batch.BatchId, null, BatchDiagnosticOperation.Run, BatchDiagnosticPhase.Returned);
        }
    }

    private async Task<ShellViewModel.GoogleTtsStudioRequestSelection> RefreshBatchSelectionOnUiAsync(
        ShellViewModel model, ShellViewModel.GoogleTtsStudioRequestSelection original, CancellationToken token) =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await RefreshExactVoiceAsync(model, original, token).ConfigureAwait(true);
            var voice = model.TryCaptureBatchVoice(out _) ?? throw new InvalidOperationException("The batch voice is no longer verified. No request was sent.");
            return ShellViewModel.ForBatchDocument(voice, original.DocumentId, original.RevisionId!.Value, original.ExactText);
        }).ConfigureAwait(false);

    /// <summary>Checks everything the batch needs without contacting Google or committing anything: the voice, each
    /// file's real admission and maximum in its engine, the output folder (by writing and removing a small probe file of
    /// its own), names, audio tools and the approval. The verdict counts every blocking line, not only the files.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "A linear checklist; each line is one independent read-only check.")]
    private async Task<BatchRehearsalReport> RehearseBatchAsync(BatchSession session, ShellViewModel model)
    {
        var report = new BatchRehearsalReport();
        if (session.Batch is not { } batch || documentLibrary is null) { report.Block("Save the batch first."); return report; }
        var voice = model.TryCaptureBatchVoice(out string? problem);
        if (voice is null) report.Block(problem ?? "Select the batch voice.");
        else if (!string.Equals(voice.VoiceStableId, batch.Settings.VoiceStableId, StringComparison.Ordinal))
            report.Block($"Select the batch voice {batch.Settings.VoiceLabel} (Use this batch's voice).");
        else report.Pass("The batch voice is selected and verified.");
        var (writable, folderText) = ProbeWritable(batch.OutputFolder, batch.Included.Any(x => x.State is BatchItemState.Ready or BatchItemState.Running
            && string.Equals(x.Engine ?? batch.Settings.Engine, BatchEngines.CompleteArticle, StringComparison.Ordinal)));
        if (writable) report.Pass(folderText); else report.Block(folderText);
        var clashes = batch.Included.Where(x => BatchEditor.CanArrange(x) && File.Exists(Path.Combine(batch.OutputFolder, x.OutputFileName))).ToArray();
        if (clashes.Length == 0) report.Pass("No output name clashes with an existing file.");
        else report.Note($"{clashes.Length} output name(s) now exist; they get a number in brackets when you start. Nothing is overwritten.");
        int pending = batch.Items.Count(BatchDocumentImporter.NeedsImport);
        if (pending > 0) report.Block($"{pending} file(s) are not in the library yet; reopen the batch to finish adding them, or remove them.");
        var tools = await MediaToolsStore.InspectAsync().ConfigureAwait(true);
        if (tools.Selection is not null) report.Pass("Your saved audio tools are ready.");
        else report.Block("Audio tools are not verified. Choose ffmpeg.exe with ffprobe.exe when you start, then rehearse again.");
        if (session.Profile is null)
            report.Block("Batch spending is not set up on this computer, so a paid batch cannot start. Preparing and rehearsing cost nothing.");
        var consent = await session.Gate.LoadConsentAsync(batch.BatchId).ConfigureAwait(true);
        foreach (var item in batch.Items.Where(x => x.State is BatchItemState.Failed or BatchItemState.Uncertain && !BatchDocumentImporter.NeedsImport(x)).OrderBy(x => x.Order))
            report.Note(item.State == BatchItemState.Failed
                ? $"File {item.Order:00} failed; Resume skips it until you choose Retry ({BatchDialogPresentation.Reason(item.ReasonCode ?? "batch.paused")})."
                : $"File {item.Order:00} needs checking; it is never resent automatically. Check again marks it for rechecking when you resume.");
        foreach (var item in batch.Items.Where(x => x.State is BatchItemState.Ready or BatchItemState.Running && !BatchDocumentImporter.NeedsImport(x)).OrderBy(x => x.Order))
        {
            var approved = consent?.Items.SingleOrDefault(x => x.ItemId == item.ItemId);
            string label = $"File {item.Order:00} ({BatchEngines.Describe(approved?.Engine ?? item.Engine)})";
            if (approved is null) { report.File(false, $"{label}: not approved yet."); continue; }
            try
            {
                if (approved.LongAudio is { } la && longAudioDocuments is not null)
                {
                    var review = await longAudioDocuments.ReviewAsync(item.DocumentId!.Value).ConfigureAwait(true);
                    bool same = review.Request.AttemptId == la.AttemptId && string.Equals(review.Request.BindingSha256, la.RequestBindingSha256, StringComparison.Ordinal);
                    bool within = review.Cost.IsAlreadyCommitted || review.Cost.EstimatedSynthesisUsdCents <= la.CeilingUsdCents;
                    report.File(same && within, same && within ? $"{label}: admitted by Long Audio; exact estimate within its approved ceiling." : $"{label}: the Long Audio request differs from its approval.");
                }
                else if (string.Equals(approved.Engine, BatchEngines.CompleteArticle, StringComparison.Ordinal) && chirpArticles is not null)
                {
                    if (item.JobId is null)
                    {
                        string destination = item.OutputPath ?? Path.Combine(batch.OutputFolder, item.OutputFileName);
                        _ = ArticleAudioExportService.PreflightDestination(destination);
                        if (tools.Selection is { } approvedTools)
                            await ArticleAudioExportService.PreflightGenerationAsync(destination, approvedTools.EncoderPath, approvedTools).ConfigureAwait(true);
                    }
                    var review = await chirpArticles.PrepareWholeArticleAsync(item.DocumentId!.Value).ConfigureAwait(true);
                    bool fits = string.Equals(review.PlanSha256, approved.PlanSha256, StringComparison.Ordinal) && review.MaximumAdditionalMinorUnits <= approved.MaximumMinorUnits;
                    report.File(fits, fits ? $"{label}: admitted; exact maximum within its approval." : $"{label}: its exact plan or maximum differs from its approval.");
                }
                else report.File(false, $"{label}: this engine is not available in this window.");
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or GoogleLongAudioBudgetExceededException)
            { report.File(false, $"{label}: not admitted ({error.Message})"); }
        }
        if (voice is not null)
        {
            var decision = await session.Gate.CheckAsync(batch, voice.AccountStableId, voice.ProjectStableId).ConfigureAwait(true);
            if (decision.Allowed) report.Pass(decision.Explanation); else report.Block(decision.Explanation);
        }
        return report;
    }

    private static (bool Writable, string Text) ProbeWritable(string folder, bool requirePhysicalArticleOutput)
    {
        if (!Directory.Exists(folder)) return (false, "The output folder is missing. Choose another output folder.");
        string probe = Path.Combine(folder, ".cloudscribe-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (requirePhysicalArticleOutput) _ = ArticleAudioExportService.PreflightDestination(Path.ChangeExtension(probe, ".mp3"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return (true, "The output folder exists and is writable.");
        }
        catch (InvalidOperationException)
        { return (false, "Complete-article output requires an existing physical folder. Linked or cloud-placeholder output paths are not supported; choose another folder."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (false, "The output folder could not pass its local check: " + AudioOutputPresentation.RecoveryAdvice(error)); }
    }
}

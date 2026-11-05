using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private bool _wholeArticleDialogOpen;

    private string TrendHistoryStatus() => trendHistory is null ? string.Empty
        : $" Durable trend history: {trendHistory.FailureReason}; available={trendHistory.IsAvailable}; partial tail={trendHistory.HasPartialTail}; storage code={trendHistory.LastFailureHResult?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}. Safe structural records have no automatic expiry or count rotation; encrypted full responses expire after 24 hours.";

    private Button AddWholeArticleControl(Window owner, Panel host, ShellViewModel model)
    {
        var button = NewButton("Generate complete article MP3…");
        button.Content = "Review full article and cost";
        button.Classes.Add("primary");
        AutomationProperties.SetName(button, "Review full article and cost");
        button.Click += async (_, _) => await OpenWholeArticleAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(button);
        return button;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One modal owns the awaited consent, picker and cancellation lifetime; financial decisions remain in separately tested transactional services.")]
    private async Task OpenWholeArticleAsync(Window owner, ShellViewModel model)
    {
        if (_wholeArticleDialogOpen) return;
        _wholeArticleDialogOpen = true;
        using var lifetime = new CancellationTokenSource();
        Task? active = null;
        try
        {
            if (chirpArticles is null || chirpPreparation is null || wholeArticleRequests is null || wholeArticleResolver is null)
                throw new InvalidOperationException("Complete-article services are unavailable.");
            if (model.IsConnectedVoiceCatalogLoading) throw new InvalidOperationException("Wait for the current voice check to finish before reviewing the complete article.");
            if (model.IsDocumentDirty || model.IsDocumentSaving || model.CurrentDocumentId is not { } document)
                throw new InvalidOperationException("Save and select the original article first.");
            ChirpWholeArticleReview review;
            try { review = await chirpArticles.PrepareWholeArticleAsync(document, lifetime.Token).ConfigureAwait(true); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            {
                await OpenReadOnlyArticleReviewAsync(owner, model, document, error, lifetime.Token).ConfigureAwait(true);
                return;
            }
            var dialog = CreateArticleExportDialog(); dialog.Title = "Generate complete article MP3"; dialog.Width = 720; dialog.Height = 620;
            var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            var estimate = NewDescription($"Article: {model.DocumentTitle}. " + WholeArticleReviewPresentation.Estimate(review));
            AutomationProperties.SetName(estimate, "Article estimate and spending limit");
            var status = NewDescription("Choose where to save the complete MP3. No article job or paid request has started.");
            AutomationProperties.SetName(status, "Article setup and generation progress");
            var details = new Expander
            {
                Header = "Budget and recovery details",
                Content = NewDescription(WholeArticleReviewPresentation.Details(review))
            };
            var captureConsent = new CheckBox { Content = NewDescription(WholeArticleReviewPresentation.CaptureDescription(review)) };
            AutomationProperties.SetName(captureConsent, review.MaximumRecoveryRequests > 0
                ? "Opt in to bounded encrypted error evidence for this article recovery" : "Opt in to bounded encrypted first-error evidence for this exact article");
            var captureWarning = NewDescription(review.MaximumRecoveryRequests > 0
                ? "Select the private evidence option to enable automatic split recovery. Required error evidence is checked before another reservation. Existing one-response consent is not reused."
                : "Private capture is off unless you select the separate option. Routine metadata cannot preserve the full provider explanation. Existing single-request consent does not cover this article.");
            var choose = NewButton("Choose where to save the complete MP3");
            var generate = NewButton("Generate complete article MP3"); generate.IsEnabled = false;
            string? destination = null, ffmpeg = null;
            bool selecting = false, startAttempted = false;
            bool ReviewExpired() => !review.CompletionBound && (DateTimeOffset.UtcNow >= review.ConfirmByUtc || DateTimeOffset.UtcNow >= review.ExpiresAtUtc);
            void Update() => generate.IsEnabled = !startAttempted && !selecting && !ReviewExpired()
                && destination is not null && ffmpeg is not null && active is not { IsCompleted: false }
                && (review.MaximumRecoveryRequests == 0 || captureConsent.IsChecked == true);
            captureConsent.PropertyChanged += (_, change) =>
            { if (change.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) Update(); };
            choose.Click += async (_, _) =>
            {
                if (selecting || active is { IsCompleted: false }) return;
                selecting = true; choose.IsEnabled = false; Update();
                try
                {
                    var tool = await ChooseExistingMediaToolsAsync(dialog, status).ConfigureAwait(true);
                    if (tool is null) return;
                    var file = await dialog.StorageProvider.SaveFilePickerAsync(new()
                    {
                        Title = "Save complete article MP3",
                        SuggestedFileName = "complete-article.mp3",
                        DefaultExtension = "mp3",
                        FileTypeChoices = [Mp3FileType],
                        ShowOverwritePrompt = true
                    }).ConfigureAwait(true);
                    if (file is null || !file.Path.IsFile) { status.Text = "Output selection cancelled; no article job or paid request started."; return; }
                    ffmpeg = tool; destination = file.Path.LocalPath; Update();
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { status.Text = ReportLocalFailure(LocalRequestStage.Export, error); }
                finally { selecting = false; choose.IsEnabled = !startAttempted; Update(); }
            };
            var close = NewButton("Close"); close.Click += (_, _) => dialog.Close();
            var stop = NewButton("Stop after current reserved request"); stop.IsEnabled = false;
            ToolTip.SetTip(stop, "The current reserved request may still be sent. Its outcome and saved audio are reconciled before stopping; no following request is reserved.");
            stop.Click += (_, _) => lifetime.Cancel();
            Guid? completedJob = null;
            var outputControls = new StackPanel { Spacing = 8, IsVisible = false };
            var refreshOutput = AddCompletedArticleOutputControls(dialog, outputControls, () => completedJob, status);
            async Task GenerateAsync()
            {
                if (model.IsConnectedVoiceCatalogLoading || model.SelectedVoiceLabVoice is not { } selectedVoice
                    || !string.Equals(selectedVoice.VoiceStableId, review.VoiceName, StringComparison.Ordinal))
                { status.Text = "The reviewed voice is not selected or its connection check is still running. Close this review, refresh voices and select the displayed voice, then review again. No article job or paid request was started."; return; }
                if (model.IsDocumentDirty || model.IsDocumentSaving)
                { status.Text = "Save the article, close this review and review the saved text again. No article job or paid request was started."; return; }
                if (destination is null || ffmpeg is null)
                { status.Text = "Before Generate, choose a new MP3 filename and an installed ffmpeg.exe with ffprobe.exe beside it. No article job or paid request was started."; return; }
                (string Destination, string Ffmpeg, string Ffprobe) exportPaths;
                try { exportPaths = ArticleAudioExportService.PreflightExport(destination, ffmpeg); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    status.Text = ReportLocalFailure(LocalRequestStage.Export, error)
                        + " Choose a new MP3 filename in an existing folder and verify the local media tools. No article job or paid request was started.";
                    return;
                }
                generate.IsEnabled = choose.IsEnabled = captureConsent.IsEnabled = close.IsEnabled = false; stop.IsEnabled = true;
                bool jobStartAttempted = false;
                var failureStage = LocalRequestStage.ArticleOpen; int failedOrdinal = 0; bool showingProgress = true;
                try
                {
                    await model.RunWholeArticleOperationAsync(async (selection, token) =>
                    {
                        if (selection.DocumentId != review.DocumentId) throw new InvalidOperationException("The selected article changed after review.");
                        failureStage = LocalRequestStage.Reservation;
                        jobStartAttempted = true; // A lost acknowledgement must not re-enable an uncertain job start.
                        startAttempted = true;
                        // This explicit Generate click confirms the single displayed estimate and retained holds.
                        // The unchanged article grant still bounds all internal reservations.
                        var job = await chirpArticles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256,
                            review.MaximumAdditionalMinorUnits, true, true), token).ConfigureAwait(true);
                        failureStage = LocalRequestStage.CaptureArm;
                        using var capture = captureConsent.IsChecked == true && review.RemainingRequests > 0
                            ? new PrivateArticleErrorCaptureScope(privateDiagnostics, TimeProvider.System, job,
                                selection.RevisionId ?? throw new InvalidOperationException("A saved revision is required for private article capture."),
                                selection.AccountStableId, selection.ProjectStableId, selection.VoiceStableId,
                                Math.Min(10_000, checked(review.RemainingRequests + review.MaximumRecoveryRequests)), explicitlyConfirmed: true,
                                allowConfirmedErrorContinuation: review.MaximumRecoveryRequests > 0)
                            : null;
                        var runtime = new ChirpWholeArticleRuntime(chirpPreparation, chirpArticles, wholeArticleRequests,
                            wholeArticleResolver, articleAudio, selection, exportPaths.Destination, exportPaths.Ffmpeg,
                            () => !model.HasCurrentVoiceLanguageCatalog,
                            token => RefreshWholeArticleSelectionOnUiAsync(model, selection, token), capture, catalogLogger,
                            (stage, ordinal) =>
                            {
                                failedOrdinal = ordinal;
                                failureStage = stage switch
                                {
                                    ArticleExecutionStage.Reservation => LocalRequestStage.Reservation,
                                    ArticleExecutionStage.Provider => LocalRequestStage.GenerationDispatch,
                                    ArticleExecutionStage.AcceptedOutputSave => LocalRequestStage.OutputRepair,
                                    ArticleExecutionStage.Assembly or ArticleExecutionStage.OutputVerification => LocalRequestStage.Export,
                                    _ => LocalRequestStage.Review
                                };
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    if (!showingProgress) return;
                                    status.Text = stage switch
                                    {
                                        ArticleExecutionStage.Provider => $"Generating internal request {ordinal}. Saved earlier audio is preserved; waiting for the provider outcome.",
                                        ArticleExecutionStage.AcceptedOutputSave => $"Google accepted request {ordinal}. Saving and verifying its local audio; never resend it to repair a local save.",
                                        ArticleExecutionStage.Assembly => "All required audio is saved. Joining the complete article MP3 locally.",
                                        ArticleExecutionStage.OutputVerification => "Verifying the complete MP3 output locally.",
                                        _ => $"Checking {stage} for internal request {ordinal}; no duplicate paid retry."
                                    };
                                });
                            }, trendHistory);
                        var progress = new Progress<ChirpArticleProgress>(p => status.Text = $"{p.SavedCoverageParts}/{p.TotalCoverageParts} parts verified and saved. Internal requests are processed in order; accepted audio is never regenerated.");
                        var result = await chirpArticles.RunWholeArticleAsync(job.JobId, runtime, progress, token).ConfigureAwait(true);
                        completedJob = result.State == ChirpWholeArticleState.Complete ? result.JobId : null;
                        outputControls.IsVisible = completedJob is not null; refreshOutput();
                        status.Text = result.State == ChirpWholeArticleState.Complete ? $"Complete article MP3 verified: {result.Output?.Path}. Original text, accepted audio and historical holds preserved."
                            : $"Article job stopped ({result.ReasonCode}). Reconcile existing evidence before any new paid attempt.";
                    }, lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    if (error is PrivateDiagnosticNotReadyException) failureStage = LocalRequestStage.CaptureRead;
                    status.Text = ReportLocalFailure(failureStage, error) + (failedOrdinal > 0 ? $" Internal request {failedOrdinal}; keep accepted audio and holds. Reconcile this result before any fresh paid authorization." : string.Empty)
                        + TrendHistoryStatus();
                }
                finally
                {
                    showingProgress = false; stop.IsEnabled = false; close.IsEnabled = true;
                    if (!jobStartAttempted && !lifetime.IsCancellationRequested) { choose.IsEnabled = captureConsent.IsEnabled = true; Update(); }
                }
            }
            generate.Click += (_, _) => { if (generate.IsEnabled && active is not { IsCompleted: false }) active = GenerateAsync(); };
            foreach (Control control in new Control[] { estimate, details, status, choose, captureConsent, captureWarning }) body.Children.Add(control);
            body.Children.Add(outputControls);
            var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 8, 20, 16) };
            foreach (Control control in new Control[] { generate, stop, close }) { control.Margin = new Thickness(0, 0, 8, 4); actions.Children.Add(control); }
            Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
            dialog.Closing += (_, e) => { if (active is { IsCompleted: false }) e.Cancel = true; };
            var reviewTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            reviewTimer.Tick += (_, _) =>
            {
                if (startAttempted || !ReviewExpired()) return;
                generate.IsEnabled = false;
                status.Text = "This cost review expired before generation. Close this dialog and review the article again; no automatic renewal or paid retry.";
            };
            reviewTimer.Start(); dialog.Closed += (_, _) => reviewTimer.Stop();
            DialogBounds.Fit(dialog, owner);
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = ReportLocalFailure(LocalRequestStage.ArticleOpen, error); }
        finally { _wholeArticleDialogOpen = false; }
    }
}

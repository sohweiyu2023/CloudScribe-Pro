using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private bool longAudioDialogOpen;
    private async Task AddLongAudioControlAsync(Window owner, Panel host, ShellViewModel model)
    {
        if (longAudioDocuments is null || longAudioInstalled is null || model.CurrentDocumentId is not { } document) return;
        var read = await longAudioInstalled.ReadInstallationAsync().ConfigureAwait(true);
        var installed = read.Context;
        if (read.Status != GoogleLongAudioInstallationStatus.Unconfigured && installed is null)
        {
            var blocked = NewButton("Long Audio setup needs attention"); blocked.IsEnabled = false; host.Children.Add(blocked);
            host.Children.Add(NewDescription(LongAudioSetupText(read))); return;
        }
        if (installed?.Scope.DocumentJobs?.Any(x => x.DocumentId == document) != true) return; // No inert control for an unconfigured API.
        var button = NewButton("Review Long Audio and cost");
        button.Classes.Add("original-document-review");
        button.Click += async (_, _) => await OpenLongAudioAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(button);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One modal owns the visible estimate, one Generate click, stop-after-outcome and output controls; backend owns authority and durable operations.")]
    private async Task OpenLongAudioAsync(Window owner, ShellViewModel model)
    {
        if (longAudioDialogOpen || longAudioDocuments is null) return;
        longAudioDialogOpen = true;
        using var lifetime = new CancellationTokenSource(); Task? active = null; bool shown = false;
        try
        {
            if (model.IsDocumentDirty || model.IsDocumentSaving || model.CurrentDocumentId is not { } document)
                throw new InvalidOperationException("Save and select the original document first.");
            var review = await longAudioDocuments.ReviewAsync(document, lifetime.Token).ConfigureAwait(true);
            var dialog = CreateArticleExportDialog(); dialog.Title = "Long Audio: complete saved document"; dialog.Width = 720; dialog.Height = 620;
            var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            var estimate = NewDescription(LongAudioReviewText(review));
            var headline = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(headline, "Long Audio current stage");
            var stepper = new WrapPanel { Orientation = Orientation.Horizontal };
            var pills = new List<Border>();
            foreach (string step in LongAudioDialogPresentation.Steps)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = step },
                };
                pills.Add(pill); stepper.Children.Add(pill);
            }
            var lastStage = LongAudioWorkflowStage.Reviewing;
            void ShowStage(LongAudioWorkflowStage stage)
            {
                lastStage = stage;
                headline.Text = LongAudioDialogPresentation.Headline(stage);
                int current = LongAudioDialogPresentation.StepIndex(stage);
                for (int i = 0; i < pills.Count; i++)
                {
                    bool now = i == current, done = current >= 0 && i < current;
                    pills[i].Background = new SolidColorBrush(Color.Parse(now ? "#FFC857" : done ? "#3348BDEB" : "#00000000"));
                    pills[i].BorderBrush = new SolidColorBrush(Color.Parse(now ? "#FFC857" : done ? "#48BDEB" : "#766485"));
                    if (pills[i].Child is TextBlock label)
                    {
                        label.Foreground = now ? new SolidColorBrush(Color.Parse("#1C182B")) : null;
                        label.FontWeight = now ? FontWeight.SemiBold : FontWeight.Normal;
                        label.Text = (done ? "\u2713 " : string.Empty) + LongAudioDialogPresentation.Steps[i];
                    }
                    AutomationProperties.SetName(pills[i], LongAudioDialogPresentation.Steps[i] + ": " + LongAudioDialogPresentation.StepState(i, current));
                }
            }
            ShowStage(LongAudioWorkflowStage.Reviewing);
            var status = NewDescription("Review the estimate, select the private evidence option and check audio setup. No request has started.");
            AutomationProperties.SetName(status, "Long Audio progress and next step");
            var coverage = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12, IsIndeterminate = true };
            AutomationProperties.SetName(coverage, "Verified source coverage; not provider processing percentage");
            var coverageText = NewDescription("Waiting for verified source coverage. Google does not expose a processing percentage.");
            var summaryText = NewDescription("Reading saved timing and spending summary...");
            GoogleLongAudioRunSummary? summary = null; bool summaryBusy = false, summaryUnavailable = false;
            System.Diagnostics.Stopwatch? runClock = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int summaryTicks = 0;
            async Task RefreshSummaryAsync()
            {
                if (summaryUnavailable) { summaryText.Text = "The audio result is preserved, but its timing/cost summary could not be fully saved. Inspect local diagnostics; no actual billing total is claimed."; return; }
                if (summaryBusy) return;
                summaryBusy = true;
                try
                {
                    summary = await longAudioDocuments.InspectRunSummaryAsync(review, lifetime.Token).ConfigureAwait(true);
                    summaryText.Text = LongAudioSummaryText(summary, DateTimeOffset.UtcNow, runClock?.IsRunning == true ? runClock.Elapsed : null, review.Cost.EstimatedSynthesisUsdCents);
                }
                catch (Exception error) when (NonFatalLongAudio(error))
                { summary = null; summaryText.Text = "Timing/spending summary is temporarily unavailable. Saved audio and financial records are unchanged. " + ReportLocalFailure(LocalRequestStage.Review, error); }
                finally { summaryBusy = false; }
            }
            timer.Tick += async (_, _) =>
            {
                if (!summaryUnavailable && summary is not null) summaryText.Text = LongAudioSummaryText(summary, DateTimeOffset.UtcNow, runClock?.IsRunning == true ? runClock.Elapsed : null, review.Cost.EstimatedSynthesisUsdCents);
                if (++summaryTicks >= 15) { summaryTicks = 0; await RefreshSummaryAsync().ConfigureAwait(true); }
            };
            var consent = new CheckBox { Content = NewDescription("Save bounded, encrypted, redacted provider responses privately for this job; omissions and truncation are reported. They may contain article fragments; exclude from normal logs and exports. Logical expiry is 24 hours, deletion on next access; encrypted remnants may remain while closed or in backups.") };
            var setup = NewButton("Check audio setup");
            var generate = NewButton(LongAudioStartText(review)); generate.IsEnabled = false;
            var stop = NewButton("Stop after current action"); stop.IsEnabled = false;
            var stopNow = NewButton("Stop waiting now"); stopNow.IsVisible = false;
            var stopState = new LongAudioStopButtons();
            void ApplyStopState() { stop.IsEnabled = stopState.StopEnabled; stopNow.IsVisible = stopState.StopNowVisible; stopNow.IsEnabled = stopState.StopNowEnabled; }
            ToolTip.SetTip(stopNow, "Stops waiting immediately. A request Google already received may still finish and be charged; the saved job checks its outcome before sending anything else.");
            CancellationTokenSource? activeRun = null;
            var close = NewButton("Close"); close.Click += (_, _) => dialog.Close();
            GoogleLongAudioMediaTools? tools = null; GoogleLongAudioRunControl? control = null; bool setupBusy = false, completed = false, permitted = true;
            void Update()
            {
                generate.IsEnabled = permitted && !completed && !setupBusy && tools is not null && consent.IsChecked == true && active is not { IsCompleted: false };
                generate.IsVisible = stop.IsVisible = !completed;
                if (completed) { coverage.IsIndeterminate = false; coverage.Value = 100; coverageText.Text = "Complete: all source audio verified and saved."; }
            }
            consent.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) Update(); };
            setup.Click += async (_, _) =>
            {
                if (setupBusy || active is { IsCompleted: false }) return;
                setupBusy = true; setup.IsEnabled = false; Update();
                try
                {
                    var selectedTools = await ChooseExistingMediaToolsAsync(dialog, status).ConfigureAwait(true);
                    if (selectedTools is not null)
                    {
                        string probe = Path.Combine(Path.GetDirectoryName(selectedTools.EncoderPath)!, "ffprobe.exe");
                        tools = new(selectedTools.EncoderPath, selectedTools.EncoderSha256, probe, selectedTools.ProbeSha256);
                        status.Text = "Audio setup is ready. Verified audio will be saved automatically; you can play it or save a copy afterward.";
                    }
                }
                catch (Exception error) when (NonFatalLongAudio(error)) { status.Text = ReportLocalFailure(LocalRequestStage.Export, error); }
                finally { setupBusy = false; setup.IsEnabled = true; Update(); }
            };
            var outputs = new StackPanel { Spacing = 8 };
            var refreshOutput = AddLongAudioOutputControls(dialog, outputs, review, status, model);
            completed = await refreshOutput().ConfigureAwait(true);
            var initial = await longAudioDocuments.InspectContinuationAsync(review, lifetime.Token).ConfigureAwait(true);
            permitted = initial.Kind != GoogleLongAudioContinuationKind.ReconcileRequired;
            if (completed) { setup.IsVisible = consent.IsVisible = false; estimate.Text = LongAudioCompletedReviewText(review); status.Text = LongAudioProgressText(new(LongAudioWorkflowStage.Completed, initial.Checkpoint), review.DeferredUnknownRanges is { Count: > 0 }); ShowStage(LongAudioWorkflowStage.Completed); }
            else if (!permitted) { setup.IsEnabled = consent.IsEnabled = false; status.Text = LongAudioProgressText(new(LongAudioWorkflowStage.Failed, initial.Checkpoint)); ShowStage(LongAudioWorkflowStage.Failed); }
            async Task GenerateAsync()
            {
                if (tools is null) return;
                control = new(); setup.IsEnabled = consent.IsEnabled = generate.IsEnabled = close.IsEnabled = false; stopState.RunStarted(); ApplyStopState();
                using var runCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); activeRun = runCancel;
                bool showing = true, fundingStopped = false; runClock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await model.RunLongAudioOperationAsync(document, async token =>
                    {
                        var progress = new Progress<GoogleLongAudioDocumentProgress>(p =>
                        {
                            if (!showing) return;
                            status.Text = LongAudioProgressText(p, review.DeferredUnknownRanges is { Count: > 0 }); ShowStage(p.Stage);
                            if (p.TotalScalars is > 0 && p.VerifiedScalars is { } saved)
                            {
                                coverage.IsIndeterminate = false; coverage.Value = Math.Clamp(100d * saved / p.TotalScalars.Value, 0, 100);
                                coverageText.Text = $"Verified source coverage: {saved:N0} / {p.TotalScalars:N0} characters ({coverage.Value:0.0}%). Final assembly and verification follow.";
                            }
                        });
                        var result = await longAudioDocuments.RunAsync(review, tools, true, control, progress, token).ConfigureAwait(true);
                        status.Text = LongAudioProgressText(new(result.Stage, result.Outcome.Checkpoint), review.DeferredUnknownRanges is { Count: > 0 }); ShowStage(result.Stage);
                        summaryUnavailable = result.SummaryUnavailable;
                        if (result.Outcome.Artifact is not null || result.ArticleOutput is not null) completed = await refreshOutput().ConfigureAwait(true);
                    }, runCancel.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (runCancel.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    ShowStage(LongAudioWorkflowStage.Stopped);
                    status.Text = "Stopped waiting. Saved audio and spending records are unchanged. If a request had already reached Google, it may still finish and be charged; reopening this document checks that saved outcome before anything else is sent.";
                }
                catch (Exception error) when (NonFatalLongAudio(error))
                {
                    fundingStopped = error is GoogleLongAudioBudgetExceededException; ShowStage(LongAudioWorkflowStage.Failed);
                    status.Text = ReportLongAudioFailure(LocalRequestStage.GenerationDispatch, error) + " Saved operations and holds are preserved; do not start a duplicate job.";
                }
                finally
                {
                    showing = false; runClock.Stop(); stopState.RunEnded(); ApplyStopState(); activeRun = null;
                    try
                    {
                        var continuation = await longAudioDocuments.InspectContinuationAsync(review, lifetime.Token).ConfigureAwait(true);
                        review = continuation.Review; estimate.Text = completed ? LongAudioCompletedReviewText(review) : LongAudioReviewText(review);
                        permitted = !fundingStopped && continuation.Kind != GoogleLongAudioContinuationKind.ReconcileRequired;
                        setup.IsEnabled = consent.IsEnabled = permitted && !completed;
                        setup.IsVisible = consent.IsVisible = !completed;
                        generate.Content = LongAudioStartText(review);
                    }
                    catch (Exception error) when (NonFatalLongAudio(error))
                    { permitted = false; var message = ReportLongAudioFailure(LocalRequestStage.Review, error); if (!fundingStopped) status.Text = message + "\nKeep this saved job and its evidence; no automatic retry was made."; }
                    active = null; close.IsEnabled = true; Update(); await RefreshSummaryAsync().ConfigureAwait(true);
                }
            }
            generate.Click += (_, _) => { if (generate.IsEnabled && active is not { IsCompleted: false }) active = GenerateAsync(); };
            stop.Click += (_, _) =>
            {
                control?.RequestStop(); stopState.StopRequested(activeRun is not null); ApplyStopState();
                status.Text = "Stopping after the current step is saved. A request already sent is not cancelled or repeated. If this takes too long, choose Stop waiting now.";
            };
            stopNow.Click += (_, _) =>
            {
                if (activeRun is not { } run || run.IsCancellationRequested || !stopState.StopNowClicked()) return;
                ApplyStopState();
                if (catalogLogger is not null) CloudScribeLog.LongAudioUserStoppedWaiting(catalogLogger, review.Request.AttemptId, lastStage);
                run.Cancel();
            };
            foreach (var item in new Control[] { headline, stepper, status, coverage, coverageText, outputs, summaryText }) body.Children.Add(item);
            AddSection(body, "Cost, spending cap and recovery details", expanded: !completed).Children.Add(estimate);
            foreach (var item in new Control[] { setup, consent }) body.Children.Add(item);
            var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            layout.Children.Add(new ScrollViewer { Content = body });
            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 8, 20, 16) };
            foreach (var button in new[] { generate, stop, stopNow, close }) { button.Margin = new Thickness(0, 0, 8, 4); actions.Children.Add(button); }
            var actionBar = CosmicDialogTheme.ActionBar(actions);
            Grid.SetRow(actionBar, 1); layout.Children.Add(actionBar); dialog.Content = layout;
            dialog.Closing += (_, e) =>
            {
                if (active is not { IsCompleted: false } && !setupBusy) return;
                e.Cancel = true;
                if (active is { IsCompleted: false })
                    status.Text = "A run is still active. Choose Stop after current action (or Stop waiting now), then close.";
            };
            Update(); await RefreshSummaryAsync().ConfigureAwait(true);
            DialogBounds.Fit(dialog, owner); shown = true; timer.Start();
            try { await dialog.ShowDialog(owner).ConfigureAwait(true); }
            finally { timer.Stop(); }
        }
        catch (Exception error) when (NonFatalLongAudio(error))
        {
            model.StatusMessage = ReportLongAudioFailure(LocalRequestStage.Review, error);
            if (!shown)
            {
                var notice = CreateArticleExportDialog(); notice.Title = "Long Audio review needs attention";
                var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
                body.Children.Add(NewDescription("Long Audio could not open the configured saved revision. Check the saved document and installation setup; this review sent no Google request.\n\n" + model.StatusMessage));
                body.Children.Add(CreateHistoricalCloseButton(notice)); notice.Content = new ScrollViewer { Content = body };
                DialogBounds.Fit(notice, owner); await notice.ShowDialog(owner).ConfigureAwait(true);
            }
        }
        finally { longAudioDialogOpen = false; }
    }

    private static bool NonFatalLongAudio(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
    private string ReportLongAudioFailure(LocalRequestStage stage, Exception error)
    {
        if (error is not GoogleLongAudioBudgetExceededException budget) return ReportLocalFailure(stage, error);
        if (catalogLogger is not null) CloudScribeLog.LongAudioBudgetStop(catalogLogger, budget.ProvisionUsdCents, budget.CapUsdCents);
        return budget.Message;
    }
    private static async Task<string> HashToolAsync(string path)
    {
        RequirePhysicalOutputPath(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file).ConfigureAwait(false));
    }
}

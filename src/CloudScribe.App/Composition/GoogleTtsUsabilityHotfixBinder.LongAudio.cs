using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
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
            var estimate = NewDescription(LongAudioReviewText(review)); body.Children.Add(estimate);
            var status = NewDescription("Review the estimate, select the private evidence option and check audio setup. No request has started.");
            AutomationProperties.SetName(status, "Long Audio progress and next step");
            var consent = new CheckBox { Content = NewDescription("Save bounded, encrypted, redacted provider responses privately for this job; omissions and truncation are reported. They may contain article fragments; exclude from normal logs and exports. Logical expiry is 24 hours, deletion on next access; encrypted remnants may remain while closed or in backups.") };
            var setup = NewButton("Check audio setup");
            var generate = NewButton(LongAudioStartText(review)); generate.IsEnabled = false;
            var stop = NewButton("Stop after current action"); stop.IsEnabled = false;
            var close = NewButton("Close"); close.Click += (_, _) => dialog.Close();
            GoogleLongAudioMediaTools? tools = null; GoogleLongAudioRunControl? control = null; bool setupBusy = false, completed = false, permitted = true;
            void Update() => generate.IsEnabled = permitted && !completed && !setupBusy && tools is not null && consent.IsChecked == true && active is not { IsCompleted: false };
            consent.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) Update(); };
            setup.Click += async (_, _) =>
            {
                if (setupBusy || active is { IsCompleted: false }) return;
                setupBusy = true; setup.IsEnabled = false; Update();
                try
                {
                    string? executable = await ChooseExistingMediaToolsAsync(dialog, status).ConfigureAwait(true);
                    if (executable is not null)
                    {
                        string probe = Path.Combine(Path.GetDirectoryName(executable)!, "ffprobe.exe");
                        tools = new(executable, await HashToolAsync(executable).ConfigureAwait(true), probe, await HashToolAsync(probe).ConfigureAwait(true));
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
            if (completed) { setup.IsVisible = consent.IsVisible = false; estimate.Text = LongAudioCompletedReviewText(review); status.Text = LongAudioProgressText(new(LongAudioWorkflowStage.Completed, initial.Checkpoint)); }
            else if (!permitted) { setup.IsEnabled = consent.IsEnabled = false; status.Text = LongAudioProgressText(new(LongAudioWorkflowStage.Failed, initial.Checkpoint)); }
            async Task GenerateAsync()
            {
                if (tools is null) return;
                control = new(); setup.IsEnabled = consent.IsEnabled = generate.IsEnabled = close.IsEnabled = false; stop.IsEnabled = true;
                bool showing = true;
                try
                {
                    await model.RunLongAudioOperationAsync(document, async token =>
                    {
                        var progress = new Progress<GoogleLongAudioDocumentProgress>(p => { if (showing) status.Text = LongAudioProgressText(p); });
                        var result = await longAudioDocuments.RunAsync(review, tools, true, control, progress, token).ConfigureAwait(true);
                        status.Text = LongAudioProgressText(new(result.Stage, result.Outcome.Checkpoint));
                        if (result.Outcome.Artifact is not null || result.ArticleOutput is not null) completed = await refreshOutput().ConfigureAwait(true);
                    }, lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception error) when (NonFatalLongAudio(error)) { status.Text = ReportLocalFailure(LocalRequestStage.GenerationDispatch, error) + " Saved operations and holds are preserved; do not start a duplicate job."; }
                finally
                {
                    showing = false; stop.IsEnabled = false;
                    try
                    {
                        var continuation = await longAudioDocuments.InspectContinuationAsync(review, lifetime.Token).ConfigureAwait(true);
                        review = continuation.Review; estimate.Text = completed ? LongAudioCompletedReviewText(review) : LongAudioReviewText(review);
                        permitted = continuation.Kind != GoogleLongAudioContinuationKind.ReconcileRequired;
                        setup.IsEnabled = consent.IsEnabled = permitted && !completed;
                        setup.IsVisible = consent.IsVisible = !completed;
                        generate.Content = LongAudioStartText(review);
                    }
                    catch (Exception error) when (NonFatalLongAudio(error))
                    { permitted = false; _ = ReportLocalFailure(LocalRequestStage.Review, error); status.Text += "\nContinuation could not be verified. Keep this job and inspect its saved evidence."; }
                    active = null; close.IsEnabled = true; Update();
                }
            }
            generate.Click += (_, _) => { if (generate.IsEnabled && active is not { IsCompleted: false }) active = GenerateAsync(); };
            stop.Click += (_, _) => { control?.RequestStop(); stop.IsEnabled = false; status.Text = "Stopping after the current submission/status/output action is recorded. The in-flight paid request is not cancelled or repeated."; };
            foreach (var item in new Control[] { status, setup, consent, outputs }) body.Children.Add(item);
            var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            layout.Children.Add(new ScrollViewer { Content = body });
            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 8, 20, 16) };
            foreach (var button in new[] { generate, stop, close }) { button.Margin = new Thickness(0, 0, 8, 4); actions.Children.Add(button); }
            Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
            dialog.Closing += (_, e) => { if (active is { IsCompleted: false } || setupBusy) e.Cancel = true; };
            DialogBounds.Fit(dialog, owner); shown = true; await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        catch (Exception error) when (NonFatalLongAudio(error))
        {
            model.StatusMessage = ReportLocalFailure(LocalRequestStage.Review, error);
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
    private static async Task<string> HashToolAsync(string path)
    {
        RequirePhysicalOutputPath(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file).ConfigureAwait(false));
    }
}

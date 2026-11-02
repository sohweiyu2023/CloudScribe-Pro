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

    private void AddWholeArticleControl(Window owner, StackPanel host, ShellViewModel model)
    {
        var button = NewButton("Generate complete article MP3…");
        AutomationProperties.SetName(button, "Review and generate one complete article MP3");
        button.Click += async (_, _) => await OpenWholeArticleAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(button);
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
            var review = await chirpArticles.PrepareWholeArticleAsync(document, lifetime.Token).ConfigureAwait(true);
            var dialog = CreateArticleExportDialog(); dialog.Title = "Generate complete article MP3"; dialog.Width = 720; dialog.Height = 620;
            var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
            var status = NewDescription($"Voice: {review.VoiceName}. {review.AcceptedParts}/{review.TotalParts} parts already accepted; {review.RemainingRequests} internal requests remain. "
                + $"Maximum new cost: {review.Currency} {review.MaximumAdditionalMinorUnits / 100m:F2} before tax. Existing commitments: {review.Currency} {review.RetainedCommitmentsMinorUnits / 100m:F2}, retained. "
                + $"Confirm this review by {review.ConfirmByUtc:O}. Independent authority expires {review.ExpiresAtUtc:O}. Up to {review.MaximumCatalogRefreshes} non-speech catalog/bootstrap refreshes may occur between completed requests, including an empty capability-validation request. Stops on failure or uncertainty; no automatic paid retry. Accepted audio is reused. Final output is one continuous MP3, fully decoded locally.");
            AutomationProperties.SetName(status, "Whole article cost, retained holds and generation progress");
            var consent = new CheckBox { Content = new TextBlock { Text = "I authorize this complete article with the displayed voice and maximum cost, preserving all historical holds.", TextWrapping = TextWrapping.Wrap, MaxWidth = 620 } };
            AutomationProperties.SetName(consent, "Approve the displayed complete article and maximum cost");
            var choose = NewButton("Choose MP3 destination and installed FFmpeg…");
            var generate = NewButton("Generate complete article MP3"); generate.IsEnabled = false;
            string? destination = null, ffmpeg = null;
            void Update() => generate.IsEnabled = consent.IsChecked == true && destination is not null && ffmpeg is not null && active is not { IsCompleted: false };
            consent.IsCheckedChanged += (_, _) => Update();
            choose.Click += async (_, _) =>
            {
                var tools = await dialog.StorageProvider.OpenFilePickerAsync(new()
                {
                    Title = "Select installed ffmpeg.exe",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("FFmpeg") { Patterns = ["ffmpeg.exe"] }]
                }).ConfigureAwait(true);
                if (tools.Count != 1 || !tools[0].Path.IsFile) return;
                var file = await dialog.StorageProvider.SaveFilePickerAsync(new()
                {
                    Title = "Save complete article MP3",
                    SuggestedFileName = "complete-article.mp3",
                    DefaultExtension = "mp3",
                    FileTypeChoices = [Mp3FileType],
                    ShowOverwritePrompt = true
                }).ConfigureAwait(true);
                if (file is null || !file.Path.IsFile) return;
                ffmpeg = tools[0].Path.LocalPath; destination = file.Path.LocalPath; Update();
            };
            var close = NewButton("Close"); close.Click += (_, _) => dialog.Close();
            var stop = NewButton("Stop after reconciling the current request"); stop.IsEnabled = false;
            stop.Click += (_, _) => lifetime.Cancel();
            async Task GenerateAsync()
            {
                generate.IsEnabled = choose.IsEnabled = consent.IsEnabled = close.IsEnabled = false; stop.IsEnabled = true;
                try
                {
                    if (destination is null || ffmpeg is null || File.Exists(destination) || !File.Exists(ffmpeg)
                        || !File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")))
                        throw new InvalidOperationException("Choose a new output filename and installed FFmpeg with ffprobe before Generate.");
                    await model.RunWholeArticleOperationAsync(async (selection, token) =>
                    {
                        if (selection.DocumentId != review.DocumentId) throw new InvalidOperationException("The selected article changed after review.");
                        var job = await chirpArticles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256,
                            review.MaximumAdditionalMinorUnits, true, true), token).ConfigureAwait(true);
                        var runtime = new ChirpWholeArticleRuntime(chirpPreparation, chirpArticles, wholeArticleRequests,
                            wholeArticleResolver, articleAudio, selection, destination, ffmpeg,
                            () => !model.HasCurrentVoiceLanguageCatalog,
                            token => RefreshWholeArticleSelectionOnUiAsync(model, selection, token));
                        var progress = new Progress<ChirpArticleProgress>(p => status.Text = $"{p.SavedCoverageParts}/{p.TotalCoverageParts} parts verified and saved. Internal requests are processed in order; accepted audio is never regenerated.");
                        var result = await chirpArticles.RunWholeArticleAsync(job.JobId, runtime, progress, token).ConfigureAwait(true);
                        status.Text = result.State == ChirpWholeArticleState.Complete ? $"Complete article MP3 verified: {result.Output?.Path}. Original text, accepted audio and historical holds preserved."
                            : $"Article job stopped ({result.ReasonCode}). Reconcile existing evidence before any new paid attempt.";
                    }, lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { status.Text = ReportLocalFailure(LocalRequestStage.ArticleOpen, error); }
                finally { stop.IsEnabled = false; close.IsEnabled = true; }
            }
            generate.Click += (_, _) => { if (generate.IsEnabled && active is not { IsCompleted: false }) active = GenerateAsync(); };
            foreach (Control control in new Control[] { status, choose, consent, generate, stop, close }) body.Children.Add(control);
            dialog.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            dialog.Closing += (_, e) => { if (active is { IsCompleted: false }) e.Cancel = true; };
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = ReportLocalFailure(LocalRequestStage.ArticleOpen, error); }
        finally { _wholeArticleDialogOpen = false; }
    }
}

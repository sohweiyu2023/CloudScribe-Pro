using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private bool _chirpArticleDialogOpen;

    private void AddChirpArticleControls(MainWindow owner, StackPanel host, ShellViewModel model)
    {
        var button = NewButton("Internal article narration (Chirp 3)");
        AutomationProperties.SetName(button, "Internal article narration, original text and accepted audio preserved");
        void Update() => button.IsEnabled = chirpArticles is not null && chirpPreparation is not null && model.HasOpenDocument && model.CanPrepareGoogleGenerationSpend;
        model.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ShellViewModel.HasOpenDocument) or nameof(ShellViewModel.CanPrepareGoogleGenerationSpend)) Update(); };
        button.Click += async (_, _) => await OpenChirpArticleAsync(owner, model).ConfigureAwait(true);
        host.Children.Add(NewDescription("Chirp 3 can reject sentence content even below the byte limit. Internal requests use existing boundaries without changing the article. The 333-scalar window is a local heuristic, not a provider guarantee. No extra Library documents or automatic requests."));
        host.Children.Add(button); Update();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One modal owns cancellation, exact review and awaited single-action lifetimes; financial and output behavior is independently transactional and tested.")]
    private async Task OpenChirpArticleAsync(MainWindow owner, ShellViewModel model)
    {
        if (_chirpArticleDialogOpen || chirpArticles is null || chirpPreparation is null) return;
        _chirpArticleDialogOpen = true;
        using var lifetime = new CancellationTokenSource(); Task? active = null; ChirpRequestReview? displayed = null;
        try
        {
            Guid document = model.CurrentDocumentId ?? throw new InvalidOperationException("Select the saved original article.");
            var dialog = CreateArticleExportDialog(); dialog.Title = "Internal article narration"; dialog.Width = 720; dialog.Height = 640;
            var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            var status = NewDescription("Checking saved original article and local coverage. No provider request is sent.");
            var reviewText = NewDescription(string.Empty);
            var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, "Internal article request and local acceptance status");
            var consent = new CheckBox { Content = new TextBlock { Text = "Reserve only the displayed internal request and retain every historical hold. This does not start generation or authorize automatic retries.", TextWrapping = TextWrapping.Wrap, MaxWidth = 620 } };
            AutomationProperties.SetName(consent, "Acknowledge exact internal request and retained historical holds");
            var refresh = NewButton("Refresh local coverage"); var prepare = NewButton("Review one selected internal request");
            var reserve = NewButton("Reserve displayed request only"); reserve.IsEnabled = false;
            var repair = NewButton("Recover saved local outputs (no provider call)");
            var export = NewButton("Export complete article MP3"); export.IsEnabled = false;
            ChirpArticleProgress? progress = null;
            async Task RefreshAsync()
            {
                progress = await chirpArticles.ProgressAsync(document, lifetime.Token).ConfigureAwait(true);
                choice.ItemsSource = progress.Parts.Select(x => $"{x.CoverageOrdinal:D3} | {x.Scalars} characters | {x.Status} | {(x.CurrentlyAdmitted ? "reviewed scope" : "no current paid admission")}").ToArray();
                int selected = Array.FindIndex(progress.Parts.ToArray(), x => x.CurrentlyAdmitted && !x.Reused && !x.Status.StartsWith("Saved", StringComparison.Ordinal));
                choice.SelectedIndex = selected >= 0 ? selected : progress.Parts.Count == 0 ? -1 : 0;
                status.Text = $"{progress.SavedCoverageParts}/{progress.TotalCoverageParts} exact coverage parts saved/reused. The original text and earlier audio/holds remain unchanged. Each new request needs current independent admission and exact review. Final export is one continuous MP3.";
                export.IsEnabled = progress.Complete;
            }
            async Task RunAsync(Func<Task> operation)
            {
                choice.IsEnabled = prepare.IsEnabled = reserve.IsEnabled = refresh.IsEnabled = repair.IsEnabled = export.IsEnabled = false;
                try { await operation().ConfigureAwait(true); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { status.Text = "Stopped safely. Verify the saved original, exact voice/account, current independent admission, retained budget and local output status. No automatic provider request or retry was made."; }
                finally
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        choice.IsEnabled = prepare.IsEnabled = refresh.IsEnabled = repair.IsEnabled = true;
                        reserve.IsEnabled = displayed is not null && consent.IsChecked == true;
                        export.IsEnabled = progress?.Complete == true;
                    }
                }
            }
            void Start(Func<Task> operation) { if (active is not { IsCompleted: false }) active = RunAsync(operation); }
            choice.SelectionChanged += (_, _) =>
            {
                if (displayed is not null) chirpPreparation.Discard(displayed.PreviewId);
                displayed = null; consent.IsChecked = false; reserve.IsEnabled = false; reviewText.Text = string.Empty;
            };
            consent.IsCheckedChanged += (_, _) => reserve.IsEnabled = displayed is not null && consent.IsChecked == true && active is not { IsCompleted: false };
            refresh.Click += (_, _) => Start(RefreshAsync);
            repair.Click += (_, _) => Start(async () => { int recovered = await chirpArticles.RecoverLocalOutputsAsync(lifetime.Token).ConfigureAwait(true); await RefreshAsync().ConfigureAwait(true); status.Text += $" Recovered {recovered} local receipts; no audio was regenerated."; });
            prepare.Click += (_, _) => Start(async () =>
            {
                if (progress is null || choice.SelectedIndex < 0) return;
                var part = progress.Parts[choice.SelectedIndex];
                if (part.Reused || !part.CurrentlyAdmitted) { status.Text = "This coverage is already retained or has no current paid admission. No new request was prepared."; return; }
                displayed = await model.PrepareChirpArticleAsync(part.CoverageOrdinal, lifetime.Token).ConfigureAwait(true);
                reviewText.Text = FormatChirpReview(displayed); consent.IsChecked = false;
            });
            reserve.Click += (_, _) => Start(async () =>
            {
                if (displayed is null || consent.IsChecked != true) return;
                await model.ApproveChirpArticleAsync(new(displayed.PreviewId, displayed.ReviewSha256, displayed.MaximumMinorUnits, true, true), lifetime.Token).ConfigureAwait(true);
                dialog.Close(); // Only reserves. Existing Generate remains a separate explicit action.
            });
            export.Click += (_, _) => Start(() => ExecuteArticleExportAsync(dialog, model, document, status, lifetime.Token));
            var close = CreateHistoricalCloseButton(dialog);
            foreach (Control control in new Control[] { status, choice, refresh, prepare, reviewText, consent, reserve, repair, export, close }) body.Children.Add(control);
            dialog.Closed += (_, _) => { lifetime.Cancel(); if (displayed is not null) chirpPreparation.Discard(displayed.PreviewId); };
            dialog.Content = new ScrollViewer { Content = body }; Start(RefreshAsync);
            await dialog.ShowDialog(owner).ConfigureAwait(true); if (active is not null) await active.ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = "Internal article view is unavailable. Documents, accepted audio and all financial holds were preserved."; }
        finally { _chirpArticleDialogOpen = false; }
    }

    private static string FormatChirpReview(ChirpRequestReview review) =>
        $"Internal request {review.CoverageOrdinal:D3}; {review.VoiceName}; {review.Currency} estimate {review.EstimateMinorUnits / 100m:F2}, maximum {review.MaximumMinorUnits / 100m:F2} before tax. "
        + $"Project already committed {review.ProjectCommittedMinorUnits / 100m:F2}; allocation remaining {review.AllocationRemainingMinorUnits / 100m:F2}. "
        + $"Payload {review.PayloadSha256}, {review.PayloadBytes} bytes. Admission expires {review.ExpiresAtUtc:O}. "
        + (review.ExistingAuthorizationId is null ? "This reserves one new request only." : "This resumes the SAME reserved identity without another reservation.")
        + (review.FailedParentAuthorizationId is { } parent ? $" Diagnostic successor of failed attempt {parent:N}; its historical hold is retained. One new request maximum; no automatic retry." : string.Empty);
}

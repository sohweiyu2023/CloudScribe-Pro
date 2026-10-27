using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CloudScribe.App.Diagnostics;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private async Task OpenHistoricalAudioImportAsync(Window owner, ShellViewModel model, CancellationToken parentToken)
    {
        try
        {
            var ids = await recoveryAdmission.ListHistoricalEvidenceIdsAsync(parentToken).ConfigureAwait(true);
            var dialog = CreateHistoricalImportDialog();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            dialog.Closed += (_, _) => lifetime.Cancel();
            var state = new HistoricalAudioImportReviewState(); Task? active = null;
            var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            var status = new TextBlock
            {
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Text = ids.Count == 0 ? "No independently reviewed historical evidence is admitted for this exact build and profile. Originals and billing holds stay unchanged."
                : "Select admitted evidence, then verify the original request and retained MP3. This imports local provenance only; it never generates audio or releases a billing hold."
            };
            var choice = new ComboBox { ItemsSource = ids, IsEnabled = ids.Count > 0 };
            AutomationProperties.SetName(choice, "Independently admitted historical evidence");
            var prepare = NewButton("Verify retained request and MP3…"); prepare.IsEnabled = false;
            var acknowledgement = new CheckBox { Content = "I reviewed this exact matched historical output; import it without generating audio.", IsEnabled = false };
            var import = NewButton("Import reviewed local provenance"); import.IsEnabled = false;
            var close = CreateHistoricalCloseButton(dialog);
            choice.SelectionChanged += (_, _) =>
            {
                state.SelectEvidence(choice.SelectedItem as string); acknowledgement.IsChecked = false;
                acknowledgement.IsEnabled = false; import.IsEnabled = false; prepare.IsEnabled = state.EvidenceId is not null;
            };
            acknowledgement.IsCheckedChanged += (_, _) => { state.ConfirmExactReview(acknowledgement.IsChecked == true); import.IsEnabled = state.CanImport; };
            prepare.Click += (_, _) => active = RunHistoricalReviewAsync(dialog, state, choice, prepare, acknowledgement, import, status, lifetime.Token);
            import.Click += (_, _) => active = RunHistoricalImportAsync(model, state, choice, prepare, acknowledgement, import, status, lifetime.Token);
            body.Children.Add(status); body.Children.Add(choice); body.Children.Add(prepare); body.Children.Add(acknowledgement); body.Children.Add(import); body.Children.Add(close);
            dialog.Content = new ScrollViewer { Content = body };
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (active is not null) await active.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (parentToken.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { NativeUiDiagnosticLog.Action(NativeUiOperation.Import, NativeUiPhase.Failed, error); model.StatusMessage = "Historical import is unavailable; no audio or financial record was changed."; }
    }

    private static Window CreateHistoricalImportDialog()
    {
        var dialog = CreateArticleExportDialog();
        dialog.Title = "Review historical accepted audio";
        return dialog;
    }

    private static Button CreateHistoricalCloseButton(Window dialog)
    {
        var close = NewButton("Close");
        close.Click += (_, _) => dialog.Close();
        return close;
    }

    private async Task RunHistoricalReviewAsync(Window dialog, HistoricalAudioImportReviewState state, ComboBox choice,
        Button prepare, CheckBox acknowledgement, Button import, TextBlock status, CancellationToken cancellationToken)
    {
        if (!prepare.IsEnabled || state.EvidenceId is not { } evidenceId) return;
        int version = state.SelectionVersion; choice.IsEnabled = false; prepare.IsEnabled = false;
        acknowledgement.IsChecked = false; acknowledgement.IsEnabled = false; import.IsEnabled = false;
        try
        {
            var admitted = await recoveryAdmission.LoadAdmittedAsync(evidenceId, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Admitted evidence is unavailable.");
            var anchor = JsonSerializer.Deserialize<ArticleHistoricalAudioAnchor>(admitted.AnchorBytes.Span)
                ?? throw new InvalidOperationException("Admitted evidence is unavailable.");
            var requests = await dialog.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Select the original compiled request JSON for verification",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Original request JSON") { Patterns = ["*.json"] }]
            }).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (requests.Count != 1 || !requests[0].Path.IsFile) { status.Text = "Historical review cancelled; originals unchanged."; return; }
            byte[] bytes = await ReadHistoricalPayloadAsync(requests[0].Path.LocalPath, cancellationToken).ConfigureAwait(true);
            var audio = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Select the retained accepted MP3; it will remain unchanged", AllowMultiple = false, FileTypeFilter = [Mp3FileType] }).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (audio.Count != 1 || !audio[0].Path.IsFile) { status.Text = "Historical review cancelled; originals unchanged."; return; }
            var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, anchor.OperationId, anchor.AccountId, anchor.IdempotencyKey, bytes, "mp3");
            var preview = await articleAudio.PrepareHistoricalImportAsync(evidenceId, request, audio[0].Path.LocalPath, cancellationToken).ConfigureAwait(true);
            if (!state.PresentPreview(version, preview)) return;
            acknowledgement.IsEnabled = true;
            status.Text = $"Verified retained part {preview.Ordinal}, {preview.VoiceName}, {preview.LanguageCode}, {preview.AudioBytes:N0} bytes. Confirm this exact review to import provenance. No provider call or billing reconciliation is performed.";
        }
        catch (OperationCanceledException) { status.Text = "Historical review cancelled; originals unchanged."; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { NativeUiDiagnosticLog.Action(NativeUiOperation.Import, NativeUiPhase.Failed, error); status.Text = "Historical evidence did not match the retained request, revision, grouping or audio. Nothing was imported."; }
        finally { if (!cancellationToken.IsCancellationRequested) { choice.IsEnabled = true; prepare.IsEnabled = true; } }
    }

    private async Task RunHistoricalImportAsync(ShellViewModel model, HistoricalAudioImportReviewState state, ComboBox choice,
        Button prepare, CheckBox acknowledgement, Button import, TextBlock status, CancellationToken cancellationToken)
    {
        if (!import.IsEnabled || !state.CanImport) return;
        var preview = state.TakeConfirmedPreview(); choice.IsEnabled = false; prepare.IsEnabled = false;
        acknowledgement.IsEnabled = false; import.IsEnabled = false;
        try
        {
            await articleAudio.ImportReviewedHistoricalAudioAsync(preview.PreviewId, preview.ReviewSha256, true, cancellationToken).ConfigureAwait(true);
            NativeUiDiagnosticLog.Action(NativeUiOperation.Import, NativeUiPhase.Complete);
            status.Text = "Reviewed historical provenance imported. Original audio and all financial holds are unchanged."; model.StatusMessage = status.Text;
        }
        catch (OperationCanceledException) { status.Text = "Historical import cancelled; originals unchanged."; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { NativeUiDiagnosticLog.Action(NativeUiOperation.Import, NativeUiPhase.Failed, error); status.Text = "Historical import stopped safely. Prepare a fresh review; do not regenerate audio."; }
        finally { if (!cancellationToken.IsCancellationRequested) { choice.IsEnabled = true; prepare.IsEnabled = true; acknowledgement.IsChecked = false; } }
    }

    private static async Task<byte[]> ReadHistoricalPayloadAsync(string path, CancellationToken cancellationToken)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked historical request inputs are refused.");
            current = Path.GetDirectoryName(current);
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length is < 1 or > 5000) throw new InvalidDataException("Original request exceeds its verification bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }
}

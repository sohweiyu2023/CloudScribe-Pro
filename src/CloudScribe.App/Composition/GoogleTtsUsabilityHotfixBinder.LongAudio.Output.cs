using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private Func<Task<bool>> AddLongAudioOutputControls(Window dialog, StackPanel host, GoogleLongAudioDocumentReview review, TextBlock status,
        ShellViewModel model)
    {
        var label = NewDescription("No verified Long Audio file is available for this saved revision yet.");
        var play = NewButton("Play verified Long Audio"); var copy = NewButton("Save a copy of Long Audio"); var folder = NewButton("Open saved audio folder");
        var details = NewButton("View private provider details");
        bool busy = false; (string LocalPath, string AudioSha256, long Bytes)? artifact = null;
        void Update() => play.IsEnabled = copy.IsEnabled = folder.IsEnabled = artifact is not null && !busy;
        async Task<bool> RefreshAsync()
        {
            artifact = await InspectLongAudioPlayableAsync(review).ConfigureAwait(true);
            label.Text = artifact is null ? "No verified Long Audio file is available for this saved revision yet."
                : $"Verified saved audio for {review.Title}, revision {review.RevisionId:N}, voice {review.Request.VoiceName}.\nSaved file: {artifact.Value.LocalPath}\nSize: {LongAudioDialogPresentation.ReadableSize(artifact.Value.Bytes)}.";
            Update(); return artifact is not null;
        }
        async Task UseAsync(int action)
        {
            if (busy) return; busy = true; Update();
            try
            {
                await RefreshAsync().ConfigureAwait(true);
                if (artifact is not { } audio) return;
                if (action == 0) _ = Process.Start(new ProcessStartInfo(audio.LocalPath) { UseShellExecute = true })
                    ?? throw new InvalidOperationException("Windows could not open the verified audio file.");
                else if (action == 1) await SaveLongAudioCopyAsync(dialog, review.Title, audio, status).ConfigureAwait(true);
                else OpenLocalFolder(Path.GetDirectoryName(audio.LocalPath)!, status);
            }
            catch (Exception error) when (NonFatalLongAudio(error)) { status.Text = AudioOutputPresentation.RecoveryAdvice(error) + "\n" + ReportLocalFailure(LocalRequestStage.Export, error); }
            finally { busy = false; Update(); }
        }
        play.Click += async (_, _) => await UseAsync(0).ConfigureAwait(true);
        copy.Click += async (_, _) => await UseAsync(1).ConfigureAwait(true);
        folder.Click += async (_, _) => await UseAsync(2).ConfigureAwait(true);
        details.Click += async (_, _) => await OpenPrivateDiagnosticsAsync(dialog, model, readOnly: true).ConfigureAwait(true);
        foreach (var item in new Control[] { label, play, copy, folder, details }) host.Children.Add(item);
        Update(); return RefreshAsync;
    }

    private async Task SaveLongAudioCopyAsync(Window dialog, string title,
        (string LocalPath, string AudioSha256, long Bytes) audio, TextBlock status)
    {
        string extension = Path.GetExtension(audio.LocalPath);
        var target = await dialog.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Save a copy of verified Long Audio",
            SuggestedFileName = AudioOutputPresentation.SuggestedFileName(title, extension),
            FileTypeChoices = [new FilePickerFileType("Verified audio") { Patterns = ["*" + extension] }],
            ShowOverwritePrompt = true
        }).ConfigureAwait(true);
        if (target?.Path.IsFile != true) { status.Text = "Save copy cancelled; the verified original is unchanged."; return; }
        await CopyVerifiedOutputAsync(audio.LocalPath, audio.AudioSha256, audio.Bytes,
            Path.GetFullPath(target.Path.LocalPath), extension).ConfigureAwait(true);
        status.Text = $"Saved copy hash-verified.\nSaved file: {target.Path.LocalPath}\nNo provider request or additional synthesis hold.";
    }
    private async Task<(string LocalPath, string AudioSha256, long Bytes)?> InspectLongAudioPlayableAsync(GoogleLongAudioDocumentReview review)
    {
        var article = await longAudioDocuments!.InspectArticleOutputAsync(review).ConfigureAwait(true);
        if (article is not null) return (article.Mp3Path, article.Sha256, article.Bytes);
        var single = await longAudioDocuments.InspectOutputAsync(review).ConfigureAwait(true);
        return single is null ? null : (single.LocalPath, single.AudioSha256, single.Bytes);
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Controls;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private Action AddCompletedArticleOutputControls(Window dialog, StackPanel body, Func<Guid?> completedJob, TextBlock status)
    {
        var play = NewButton("Play complete article MP3");
        var export = NewButton("Save a copy of complete article MP3");
        var folder = NewButton("Open complete MP3 folder");
        bool busy = false;
        void Refresh() => play.IsEnabled = export.IsEnabled = folder.IsEnabled = !busy && completedJob() is not null;
        async Task UseAsync(int action)
        {
            if (busy || chirpArticles is null || completedJob() is not { } id) return;
            busy = true; Refresh();
            try
            {
                var output = await chirpArticles.InspectCompletedWholeArticleOutputAsync(id).ConfigureAwait(true);
                if (action == 0)
                    _ = Process.Start(new ProcessStartInfo(output.Path) { UseShellExecute = true })
                        ?? throw new InvalidOperationException("Windows could not open the verified completed MP3.");
                else if (action == 1)
                {
                    var chosen = await dialog.StorageProvider.SaveFilePickerAsync(new()
                    {
                        Title = "Save a copy of the complete article MP3",
                        SuggestedFileName = Path.GetFileNameWithoutExtension(output.Path) + "-copy.mp3",
                        DefaultExtension = "mp3",
                        FileTypeChoices = [Mp3FileType],
                        ShowOverwritePrompt = true
                    }).ConfigureAwait(true);
                    if (chosen is null || !chosen.Path.IsFile) { status.Text = "Save copy cancelled; the verified original is unchanged."; return; }
                    string destination = Path.GetFullPath(chosen.Path.LocalPath);
                    await CopyCompleteOutputAsync(output, destination).ConfigureAwait(true);
                    status.Text = $"Complete article copied and hash-verified.\nSaved file: {destination}\nNo provider request or new charge.";
                }
                else OpenLocalFolder(Path.GetDirectoryName(output.Path)!, status);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { status.Text = AudioOutputPresentation.RecoveryAdvice(error) + "\n" + ReportLocalFailure(LocalRequestStage.Export, error); }
            finally { busy = false; Refresh(); }
        }
        play.Click += async (_, _) => await UseAsync(0).ConfigureAwait(true);
        export.Click += async (_, _) => await UseAsync(1).ConfigureAwait(true);
        folder.Click += async (_, _) => await UseAsync(2).ConfigureAwait(true);
        foreach (Control control in new Control[] { play, export, folder }) body.Children.Add(control);
        Refresh(); return Refresh;
    }

    private static void RequirePhysicalOutputPath(string path)
    {
        for (string? at = path; at is not null; at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked output locations are not supported.");
    }

    private Task CopyCompleteOutputAsync(ChirpWholeArticleOutput output, string destination) =>
        CopyVerifiedOutputAsync(output.Path, output.Sha256, output.Bytes, destination, ".mp3");

    private async Task CopyVerifiedOutputAsync(string source, string sha256, long bytes, string destination, string extension)
    {
        if (File.Exists(destination) || !string.Equals(Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new audio filename; existing files are preserved.");
        RequirePhysicalOutputPath(destination); RequirePhysicalOutputPath(source);
        string temporary = destination + ".copy-" + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (input.Length != bytes || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(input)), sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The completed output changed. Keep it for local recovery; do not regenerate speech.");
                input.Position = 0;
                await input.CopyToAsync(copy).ConfigureAwait(true); copy.Flush(flushToDisk: true); copy.Position = 0;
                if (copy.Length != bytes || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(copy)), sha256, StringComparison.Ordinal))
                    throw new IOException("The local copy could not be verified. Preserve the original complete MP3.");
            }
            RequirePhysicalOutputPath(destination); File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _ = ReportLocalFailure(LocalRequestStage.Export, error); }
        }
    }
}

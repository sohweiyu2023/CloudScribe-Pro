using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private TrustedMediaToolsStore MediaToolsStore => new(paths.RootDirectory);

    private void AddAudioToolsSettings(Window owner, Panel host)
    {
        var status = NewDescription("Your audio tools choice is remembered for this profile. Both files are checked before reuse; changed or missing tools require setup again.");
        var change = NewButton("Change audio tools");
        change.Click += async (_, _) =>
        {
            change.IsEnabled = false;
            try { _ = await ChooseExistingMediaToolsAsync(owner, status, change: true).ConfigureAwait(true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { status.Text = "Audio setup could not be saved. Choose the actual FFmpeg bin folder with both tools and check that your profile is writable. No audio job was started."; }
            finally { change.IsEnabled = true; }
        };
        host.Children.Add(status); host.Children.Add(change);
    }

    private async Task<TrustedMediaToolSelection?> ChooseExistingMediaToolsAsync(Window owner, TextBlock status, bool change = false)
    {
        var inspection = await MediaToolsStore.InspectAsync().ConfigureAwait(true);
        var remembered = inspection.Selection;
        if (!change && remembered is not null)
        {
            status.Text = "Your saved audio tools are ready; both files were checked for changes.";
            return remembered;
        }
        string? reason = change ? null : TrustedMediaToolsStore.Explain(inspection);
        if (reason is not null) status.Text = reason;
        var candidate = await FindRememberableMediaToolsAsync(new[]
        {
            inspection.RememberedEncoderPath is { } previous ? Path.GetDirectoryName(previous) : null,
            Path.Combine(AppContext.BaseDirectory, "tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "lib", "ffmpeg", "tools", "ffmpeg", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ffmpeg", "bin")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))).ConfigureAwait(true);
        if (candidate is not null)
        {
            string found = candidate.EncoderPath;
            int decision = await ConfirmExistingMediaToolAsync(owner, found, reason).ConfigureAwait(true);
            if (decision == 0) { status.Text = "Audio setup cancelled; no article job or paid request started."; return null; }
            if (decision == 1)
            {
                status.Text = "Audio setup is ready. Choose where to save your MP3; no software was downloaded or installed.";
                await MediaToolsStore.RememberAsync(candidate).ConfigureAwait(true);
                return candidate;
            }
        }
        status.Text = "Audio tools setup: choose ffmpeg.exe from an existing FFmpeg folder with ffprobe.exe beside it. Official download guide: https://ffmpeg.org/download.html. Nothing is downloaded or installed automatically.";
        if (!await ShowMediaSetupGuideAsync(owner).ConfigureAwait(true)) return null;
        return await PickMediaToolsAsync(owner, status).ConfigureAwait(true);
    }

    private async Task<TrustedMediaToolSelection?> PickMediaToolsAsync(Window owner, TextBlock status)
    {
        while (true)
        {
            var tools = await owner.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Audio tools setup: locate FFmpeg (ffprobe must be beside it)",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("FFmpeg audio tool") { Patterns = ["ffmpeg.exe"] }]
            }).ConfigureAwait(true);
            if (tools.Count != 1 || !tools[0].Path.IsFile)
            { status.Text = "Audio setup cancelled; your previous choice was preserved. No audio job was started."; return null; }
            TrustedMediaToolSelection selected;
            try { selected = await TrustedMediaToolsStore.CaptureAsync(tools[0].Path.LocalPath).ConfigureAwait(true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { status.Text = "Choose the actual FFmpeg bin folder with ordinary ffmpeg.exe and ffprobe.exe files, not a Chocolatey shortcut. No audio job was started."; return null; }
            int decision = await ConfirmExistingMediaToolAsync(owner, selected.EncoderPath, null).ConfigureAwait(true);
            if (decision == 2) continue;
            if (decision != 1)
            { status.Text = "Audio setup cancelled; your previous choice was preserved. No audio job was started."; return null; }
            await MediaToolsStore.RememberAsync(selected).ConfigureAwait(true);
            return selected;
        }
    }
    private static async Task<TrustedMediaToolSelection?> FindRememberableMediaToolsAsync(IEnumerable<string?> directories)
    {
        foreach (string? directory in directories.Take(128))
        {
            string? found = ExistingMediaTools.Find([directory]);
            if (found is null) continue;
            try { return await TrustedMediaToolsStore.CaptureAsync(found).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { /* A wrapper or changed discovery candidate is not a saved trusted binary pair. Offer the picker instead. */ }
        }
        return null;
    }

    private async Task<bool> ShowMediaSetupGuideAsync(Window owner)
    {
        var dialog = new Window
        {
            Title = "Audio tools setup",
            Width = 600,
            Height = 340,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Background = CosmicDialogTheme.CreateDialogBackground(),
        };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        var explanation = NewDescription("To join a complete article into one MP3, CloudScribe needs the free FFmpeg audio tools. Choose the installation you want to use. Your choice will be remembered. For Chocolatey installations, select the actual bin folder under lib/ffmpeg/tools/ffmpeg, not the shortcut in chocolatey/bin.\n\nIf you already have FFmpeg, choose Locate audio tools and select ffmpeg.exe in its bin folder; ffprobe.exe must be beside it. Otherwise open the official download guide and choose a Windows build. Nothing is downloaded or installed automatically, and no narration starts during setup.");
        body.Children.Add(explanation);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var guide = NewButton("Open official download guide");
        guide.Click += (_, _) =>
        {
            try { _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ffmpeg.org/download.html") { UseShellExecute = true }); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { _ = ReportLocalFailure(LocalRequestStage.Export, error); explanation.Text = "The browser could not open. Visit https://ffmpeg.org/download.html. No download or narration was started."; }
        };
        var locate = NewButton("Locate audio tools"); locate.Click += (_, _) => dialog.Close(true);
        var cancel = NewButton("Cancel"); cancel.Click += (_, _) => dialog.Close(false);
        foreach (var button in new[] { guide, locate, cancel }) { button.Margin = new Thickness(0, 0, 8, 4); actions.Children.Add(button); }
        body.Children.Add(actions); dialog.Content = new ScrollViewer { Content = body };
        DialogBounds.Fit(dialog, owner);
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    private static async Task<int> ConfirmExistingMediaToolAsync(Window owner, string path, string? reason)
    {
        var dialog = new Window
        {
            Title = "Audio setup: existing FFmpeg found",
            Width = 600,
            Height = reason is null ? 300 : 360,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Background = CosmicDialogTheme.CreateDialogBackground(),
        };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        if (reason is not null) body.Children.Add(new TextBlock { Text = reason, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = CosmicDialogTheme.Solid(CosmicDialogTheme.Gold) });
        body.Children.Add(NewDescription("CloudScribe found an existing audio-joining tool. Use it only if you recognize this installation. Finding these filenames does not verify their publisher. Your choice is remembered for this profile and checked on every use. If either tool changes or disappears, setup is required again. Nothing is downloaded or installed."));
        body.Children.Add(NewDescription(path));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in new[] { ("Use this installation", 1), ("Choose another", 2), ("Cancel", 0) })
        {
            var button = NewButton(item.Item1); button.Margin = new Thickness(0, 0, 8, 4);
            button.Click += (_, _) => dialog.Close(item.Item2); actions.Children.Add(button);
        }
        body.Children.Add(actions); dialog.Content = new ScrollViewer { Content = body };
        DialogBounds.Fit(dialog, owner);
        return await dialog.ShowDialog<int>(owner).ConfigureAwait(true);
    }
}

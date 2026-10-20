using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private sealed record QuickStartStep(string Heading, string Explanation, string? OfficialLink = null, string? LinkLabel = null);

    private static readonly QuickStartStep[] QuickStartSteps =
    [
        new("Write or import text",
            "Create a local document in Studio or import TXT, Markdown, HTML or DOCX. Type or paste a short passage. Your work saves locally as you edit; Ctrl+S makes a checkpoint."),
        new("Connect Google once",
            "To create audio, use your own Google Cloud project with Text-to-Speech enabled and billing you control. In Studio, open Advanced tools and Connect or change Google account. For a first connection, keep the suggested local names, global region and official URLs; paste your own service-account JSON and choose Configure Google TTS securely. Those names are labels on this computer. A different connection needs unused local names. Your key stays in Windows Credential Manager; do not paste it again on each visit.",
            "https://cloud.google.com/text-to-speech/docs/before-you-begin", "Open official Google setup guide"),
        new("Choose a voice",
            "Open Create MP3. You can choose a saved voice setup, then select Refresh real Google voices. Pick a voice from the returned list. A remembered name is only a preference until the current catalog confirms it. The access check expires after about 30 minutes, so refresh again when prompted."),
        new("Review cost and approve",
            "CloudScribe shows the active pricing status. If needed, expand the pricing source inside Create MP3, compare rates with Google's official pricing page, and confirm the match. Choose Review current text, voice and cost. Read the exact estimate and tick approval only if you accept that request's maximum. Prices, taxes and credits can differ on your Google bill.",
            "https://cloud.google.com/text-to-speech/pricing", "Open official Google pricing"),
        new("Create, play and save the MP3",
            "Choose Create MP3 once after reviewing. Sending text to Google may incur a charge. When an accepted MP3 appears, expand Listen, save and find audio to play it, export it or open the saved folder. After restart, older files remain on disk for normal local playback. If a request becomes uncertain, inspect Setups before trying again."),
        new("Return later and share safely",
            "Your documents, voice choices and connection metadata stay on this computer. Voice and display preferences return at startup; access checks expire and every new billable request needs fresh approval. To share CloudScribe, give your friend a clean app package and this guide. Never include your app-data profile, documents, MP3s, service-account JSON, Windows credentials or billing access. Your friend must connect their own Google account."),
    ];

    [SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Small native walkthrough window with local navigation; no provider call or document mutation.")]
    private async Task OpenQuickStartAsync(MainWindow owner, ShellViewModel model)
    {
        Window dialog = new()
        {
            Title = "CloudScribe Quick Start",
            Width = 700,
            Height = 570,
            MinWidth = 500,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        int index = 0;
        TextBlock heading = new() { FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        TextBlock progress = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        TextBlock explanation = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        TextBlock linkStatus = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        Button official = NewButton("Open official guide");
        official.Click += (_, _) =>
        {
            try
            {
                string url = QuickStartSteps[index].OfficialLink ?? throw new InvalidOperationException();
                _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            { linkStatus.Text = "Windows could not open your browser. The official address is " + QuickStartSteps[index].OfficialLink; }
        };
        StackPanel content = new() { Spacing = 20, Margin = new Thickness(24) };
        content.Children.Add(progress);
        content.Children.Add(heading);
        content.Children.Add(explanation);
        content.Children.Add(official);
        content.Children.Add(linkStatus);
        TextBlock reminder = new()
        {
            Text = "This guide can be reopened with Help. Your editor and saved files stay as they are.",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(reminder);
        Button back = NewButton("Back");
        Button next = NewButton("Next");
        Button skip = NewButton("Skip for now");
        skip.IsCancel = true;
        next.IsDefault = true;
        void ShowStep()
        {
            QuickStartStep step = QuickStartSteps[index];
            progress.Text = $"Step {index + 1} of {QuickStartSteps.Length}";
            heading.Text = step.Heading;
            explanation.Text = step.Explanation;
            official.IsVisible = step.OfficialLink is not null;
            official.Content = step.LinkLabel;
            AutomationProperties.SetName(official, step.LinkLabel ?? "Official guide");
            linkStatus.Text = string.Empty;
            back.IsEnabled = index > 0;
            next.Content = index == QuickStartSteps.Length - 1 ? "Done" : "Next";
            AutomationProperties.SetName(next, index == QuickStartSteps.Length - 1 ? "Finish Quick Start" : "Next Quick Start step");
        }
        back.Click += (_, _) => { if (index > 0) { index--; ShowStep(); } };
        next.Click += (_, _) => { if (index < QuickStartSteps.Length - 1) { index++; ShowStep(); } else dialog.Close(); };
        skip.Click += (_, _) => dialog.Close();
        StackPanel footer = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 8), Spacing = 12 };
        footer.Children.Add(back);
        footer.Children.Add(next);
        footer.Children.Add(skip);
        Grid grid = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = content });
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        dialog.Content = grid;
        ShowStep();
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        try { preferences.RememberQuickStartSeen(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { model.StatusMessage = "Quick Start closed. Your editor is unchanged, but the help preference could not be saved."; }
    }

    private async Task OpenQuickStartObservedAsync(MainWindow owner, ShellViewModel model)
    {
        try { await OpenQuickStartAsync(owner, model).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = "Quick Start could not open. Your document and saved settings are unchanged; use Help to try again."; }
    }
}

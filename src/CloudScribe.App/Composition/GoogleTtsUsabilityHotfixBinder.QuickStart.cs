using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private bool _walkthroughRunning;
    private IReadOnlyList<WalkthroughStep>? _walkthroughSteps;
    private int _walkthroughIndex;
    private AnchoredWalkthrough? _studioWalkthrough;

    private async Task OpenQuickStartAsync(MainWindow owner, ShellViewModel model)
    {
        if (_walkthroughRunning) return;
        _walkthroughRunning = true;
        try
        {
            bool saved = false;
            try
            {
                saved = (await accounts.ListAsync().ConfigureAwait(true)).Count(a => a.IsEnabled &&
                    string.Equals(a.Reference.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal)) == 1;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { model.StatusMessage = "Connection metadata could not be read. The guide will include setup without changing your connection."; }
            _walkthroughSteps = WalkthroughPlan.Build(saved);
            _walkthroughIndex = 0;
            while (_walkthroughIndex >= 0 && owner.IsVisible)
            {
                WalkthroughScreen screen = _walkthroughSteps[_walkthroughIndex].Screen;
                if (screen is WalkthroughScreen.Studio or WalkthroughScreen.Return)
                {
                    model.IsFocusReading = false;
                    model.SelectedNavigationItem = model.NavigationItems[0];
                    model.CloseTransientSurfacesCommand.Execute(null);
                    using AnchoredWalkthrough guide = new(owner, _walkthroughSteps, _walkthroughIndex);
                    _studioWalkthrough = guide;
                    try { _walkthroughIndex = await guide.Completion.ConfigureAwait(true); }
                    finally { _studioWalkthrough = null; }
                }
                else if (screen == WalkthroughScreen.Connection)
                    await OpenGuidedConnectionAsync(owner, model).ConfigureAwait(true);
                else
                    await OpenCreateMp3Async(owner, model, guided: true).ConfigureAwait(true);
                if (_walkthroughIndex == -2)
                {
                    _walkthroughSteps = WalkthroughPlan.Build(savedConnection: false);
                    _walkthroughIndex = 2;
                }
            }
            try { preferences.RememberQuickStartSeen(); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { model.StatusMessage = "Guide closed, but its seen preference could not be saved. Your existing settings file is preserved."; }
        }
        finally { _walkthroughRunning = false; _walkthroughSteps = null; }
    }

    private async Task RunGuidedDialogAsync(Window dialog, MainWindow owner)
    {
        DialogBounds.Fit(dialog, owner);
        using AnchoredWalkthrough guide = new(dialog, _walkthroughSteps!, _walkthroughIndex);
        Task shown = dialog.ShowDialog(owner);
        Task completed = await Task.WhenAny(shown, guide.Completion).ConfigureAwait(true);
        _walkthroughIndex = completed == guide.Completion ? await guide.Completion.ConfigureAwait(true) : -1;
        dialog.Close();
        await shown.ConfigureAwait(true);
    }

    private async Task OpenGuidedConnectionAsync(MainWindow owner, ShellViewModel model)
    {
        Window dialog = new()
        {
            Title = "Google connection — guided setup",
            Width = 1100,
            Height = 760,
            MinWidth = 480,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        StackPanel body = new() { Spacing = 16, Margin = new Thickness(20) };
        body.Children.Add(GoogleIllustration("GoogleProjectIllustration", "In your browser · Google Cloud project",
            ["1  Project selector → choose or create YOUR project", "2  Billing → link your billing account", "3  Search: Cloud Text-to-Speech API → Enable"]));
        body.Children.Add(GoogleIllustration("GoogleKeyIllustration", "In your browser · private service-account key",
            ["1  IAM & Admin → Service Accounts → select account", "2  Keys → Add key → Create new key", "3  JSON → Create → keep the downloaded file private"]));
        body.Children.Add(NewDescription("Illustrations of Google's Console route, checked against official documentation on 20 October 2026. Labels can change. No account data or real key is shown. Use the official links in the guide for full prerequisites and permissions."));
        AddOnboardingControls(body, model);
        TextBlock status = NewDescription(model.StatusMessage);
        body.Children.Add(status);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.StatusMessage), StringComparison.Ordinal)) status.Text = model.StatusMessage;
        };
        model.PropertyChanged += changed;
        dialog.Content = new ScrollViewer { Content = body };
        try { await RunGuidedDialogAsync(dialog, owner).ConfigureAwait(true); }
        finally { model.PropertyChanged -= changed; }
    }

    private static Border GoogleIllustration(string name, string title, string[] rows)
    {
        StackPanel content = new() { Spacing = 12, Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (string row in rows)
            content.Children.Add(new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = NewDescription(row) });
        Border diagram = new() { Name = name, BorderBrush = Brushes.LightSkyBlue, BorderThickness = new Thickness(2), Child = content };
        AutomationProperties.SetName(diagram, title);
        return diagram;
    }

    private async Task OpenQuickStartObservedAsync(MainWindow owner, ShellViewModel model)
    {
        try { await OpenQuickStartAsync(owner, model).ConfigureAwait(true); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { model.StatusMessage = "The walkthrough could not open. Your document and connection are unchanged; use Help to try again."; }
    }
}

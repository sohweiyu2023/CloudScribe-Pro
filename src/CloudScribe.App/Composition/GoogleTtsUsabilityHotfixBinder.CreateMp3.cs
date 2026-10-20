using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private async Task LoadConnectionPresentationAsync(TextBlock label, Expander setup, Button change)
    {
        try
        {
            var connected = (await accounts.ListAsync().ConfigureAwait(true))
                .Count(a => a.IsEnabled && string.Equals(a.Reference.ProviderStableId,
                    GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal));
            label.Text = connected switch
            {
                0 => "Google connection needed. Open Advanced tools to connect once.",
                1 => "Google connection saved. Your key stays in Windows Credential Manager; refresh voices when ready.",
                _ => "Several Google connections are enabled. Account selection needs attention before generating.",
            };
            setup.IsVisible = connected != 1;
            change.IsVisible = connected == 1;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            label.Text = "Could not read connection status. Open Advanced tools to inspect it; no credentials changed.";
            setup.IsVisible = true;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Guided desktop dialog layout and event wiring; approval and submission remain in the existing tested services.")]
    private async Task OpenCreateMp3Async(MainWindow owner, ShellViewModel model, bool guided = false)
    {
        if (_walkthroughRunning && !guided) return;
        Window dialog = new()
        {
            Title = "Create MP3",
            Width = guided ? 1100 : 660,
            Height = 740,
            MinWidth = 520,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(20) };
        TextBlock Text(string value) => new() { Text = value, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(Text("Choose your voice, review the cost, then create your MP3."));
        TextBlock connection = Text("Reading saved connection...");
        connection.Name = "GuideConnectionStatus";
        AutomationProperties.SetName(connection, "Google connection status");
        body.Children.Add(connection);
        await LoadConnectionStatusAsync(connection).ConfigureAwait(true);
        TextBlock document = Text(string.Empty);
        body.Children.Add(document);
        void UpdateDocument() => document.Text = string.IsNullOrWhiteSpace(model.DocumentText)
            ? "No text in the current document. Close this window and type or import text in Studio."
            : $"Current document: {model.DocumentText.Length} characters. Editing it will clear any prepared cost or approval.";
        UpdateDocument();
        model.PropertyChanged += OnDocumentChanged;
        void OnDocumentChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.DocumentText), StringComparison.Ordinal)) UpdateDocument();
        }

        ComboBox setups = new() { PlaceholderText = "Saved voice setup (optional)" };
        AutomationProperties.SetName(setups, "Saved voice setup");
        setups.ItemsSource = preferences.State.Setups.Select(s => s.Name).ToArray();
        setups.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(setups);
        setups.SelectionChanged += (_, _) =>
        {
            if (setups.SelectedItem is not string name) return;
            VoiceSetup? setup = preferences.State.Setups.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (setup is null) return;
            model.ApplySavedVoiceSetup(setup);
            try { preferences.Remember(setup); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { model.StatusMessage = "Voice loaded for this session, but its preference could not be saved."; return; }
            model.StatusMessage = "Saved voice chosen. Refresh voices to validate it before cost review.";
        };
        TextBox locale = NewTextBox("Language, for example en-US");
        locale.Text = model.VoiceLabLocaleFilter;
        TextBox search = NewTextBox("Voice search (optional)");
        search.Text = model.VoiceLabSearchText;
        TextBlock remembered = Text("Remembered voice: " + (model.PreferredVoiceName ?? "none chosen") + ". Refresh voices to validate it.");
        body.Children.Add(remembered);
        model.PropertyChanged += OnVoicePreferenceChanged;
        void OnVoicePreferenceChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.VoiceLabLocaleFilter), StringComparison.Ordinal)) locale.Text = model.VoiceLabLocaleFilter;
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.PreferredVoiceName), StringComparison.Ordinal))
                remembered.Text = "Remembered voice: " + (model.PreferredVoiceName ?? "none chosen") + ". Refresh voices to validate it.";
        }
        StackPanel filters = AddSection(body, "Language and voice filters", expanded: false);
        AddField(filters, "Language", locale);
        AddField(filters, "Search voices", search);
        AddRefreshControl(body, model, locale, search, dialog);
        AddVoiceSelector(body, model, dialog);

        TextBlock pricing = Text(model.PricingCatalogActiveSummary);
        body.Children.Add(pricing);
        model.PropertyChanged += OnPricingChanged;
        void OnPricingChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.PricingCatalogActiveSummary), StringComparison.Ordinal)) pricing.Text = model.PricingCatalogActiveSummary;
        }
        AddPricingActivationControls(AddSection(body, "Review pricing source if pricing is unavailable", expanded: false), model, dialog);

        TextBlock estimate = Text("Prepare the exact current request to see its cost. No provider generation occurs during review.");
        Button review = NewButton("Review current text, voice and cost");
        CheckBox consent = NewConfirmation("I approve this exact request and its displayed maximum cost.");
        Button create = NewButton("Create MP3 — approve and send to Google");
        create.Classes.Add("primary");
        create.Name = "GuideGenerateButton";
        ShellViewModel.GoogleGenerationSpendReview? displayed = null;
        bool busy = false;
        void Refresh()
        {
            var current = model.PreparedGoogleGenerationSpendReview;
            if (!ReferenceEquals(displayed, current))
            {
                displayed = current;
                consent.IsChecked = false;
            }
            estimate.Text = current is null
                ? "No current cost review. Refresh the voice, check pricing, then review again."
                : $"Exact request: {FormatEstimate(current)} maximum list estimate before taxes, credits and conversion. Voice: {current.VoiceName}. Each request needs new approval.";
            create.IsEnabled = !busy && !model.GenerateWithGoogleCommand.IsRunning;
            consent.IsEnabled = current is not null && !busy;
        }
        review.Click += async (_, _) =>
        {
            review.IsEnabled = false;
            try { await model.PrepareGoogleGenerationSpendReviewAsync().ConfigureAwait(true); }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { model.StatusMessage = "Cost review could not finish. Refresh voices, check the pricing source and current text. For a long document, use Advanced tools to create local parts. No generation was started."; }
            finally { review.IsEnabled = true; Refresh(); }
        };
        consent.IsCheckedChanged += (_, _) => Refresh();
        model.PropertyChanged += OnReviewChanged;
        void OnReviewChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(ShellViewModel.PreparedGoogleGenerationSpendReview)
                or nameof(ShellViewModel.CanApproveGoogleGenerationSpend)) Refresh();
        }
        create.Click += async (_, _) =>
        {
            if (busy || model.GenerateWithGoogleCommand.IsRunning) return;
            if (string.IsNullOrWhiteSpace(model.DocumentText))
            { model.StatusMessage = "Write or import text in Studio, then return to Create MP3."; return; }
            if (model.SelectedVoiceLabVoice is null)
            { model.StatusMessage = "Refresh the voice catalog and select a verified voice, then review the cost."; return; }
            if (displayed is null || !ReferenceEquals(displayed, model.PreparedGoogleGenerationSpendReview))
            { model.StatusMessage = "Review the exact current text, voice and cost before creating an MP3."; return; }
            if (consent.IsChecked != true || !model.CanApproveGoogleGenerationSpend)
            { model.StatusMessage = "Check the approval box for this exact estimate. If it changed, review again."; return; }
            busy = true;
            create.IsEnabled = false;
            try
            {
                await model.CreateReviewedGoogleMp3Async(displayed, consent.IsChecked == true).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { model.StatusMessage = "MP3 creation stopped. Review local request status in Setups before retrying; a dispatched request may still be charged."; }
            finally { busy = false; consent.IsChecked = false; Refresh(); }
        };
        body.Children.Add(review);
        TextBlock status = Text(model.StatusMessage);
        StackPanel output = AddSection(body, "Listen, save and find audio", expanded: false);
        AddVerifiedOutputControls(owner, output, model, dialog);
        Button folder = NewButton("Open saved MP3 folder");
        folder.Click += (_, _) => OpenLocalFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudScribe Pro", "Generated"), status);
        output.Children.Add(folder);
        model.PropertyChanged += OnStatusChanged;
        void OnStatusChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.StatusMessage), StringComparison.Ordinal)) status.Text = model.StatusMessage;
        }
        Button close = NewButton("Close and return to Studio");
        close.Click += (_, _) => dialog.Close();
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        ScrollViewer bodyScroll = new() { Content = body };
        layout.Children.Add(bodyScroll);
        StackPanel footer = new() { Spacing = 10, Margin = new Thickness(20, 8, 20, 16) };
        footer.Children.Add(estimate);
        footer.Children.Add(consent);
        footer.Children.Add(create);
        AddStopWaitingControl(footer, model, dialog);
        footer.Children.Add(status);
        footer.Children.Add(close);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        if (guided)
        {
            layout.Children.Clear();
            body.Children.Add(footer);
            layout.Children.Add(bodyScroll);
        }
        dialog.Content = layout;
        Refresh();
        try
        {
            if (guided) await RunGuidedDialogAsync(dialog, owner).ConfigureAwait(true);
            else await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        finally
        {
            model.PropertyChanged -= OnDocumentChanged;
            model.PropertyChanged -= OnVoicePreferenceChanged;
            model.PropertyChanged -= OnPricingChanged;
            model.PropertyChanged -= OnReviewChanged;
            model.PropertyChanged -= OnStatusChanged;
        }
    }
}

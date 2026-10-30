using System.ComponentModel;
using System.Text;
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
        if (_walkthroughRunning && !guided)
        {
            _studioWalkthrough?.NavigateToCreation();
            return;
        }
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
        Observe(body, model, (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.ConnectedVoiceCatalogStatus) or nameof(ShellViewModel.IsConnectedVoiceCatalogLoading)
                or nameof(ShellViewModel.HasCurrentVoiceLanguageCatalog))
                connection.Text = model.IsConnectedVoiceCatalogLoading
                    ? "Checking the saved Google connection. No speech is generated."
                    : (model.HasCurrentVoiceLanguageCatalog && model.VoiceLabCatalogLastRefreshSucceeded
                        ? "Google connection verified for the current catalog. "
                        : "Saved connection metadata is not proof of current access. ") + model.ConnectedVoiceCatalogStatus;
        }, dialog);
        TextBlock document = Text(string.Empty);
        body.Children.Add(document);
        void UpdateDocument() => document.Text = string.IsNullOrWhiteSpace(model.DocumentText)
            ? "No text in the current document. Close this window and type or import text in Studio."
            : $"Current document: {model.DocumentText.Length} characters. Editing it will clear any prepared cost or approval." +
                (Encoding.UTF8.GetByteCount(model.DocumentText) >= 4_000
                    ? " Long text may need parts: close this window, expand Advanced tools, then choose Preview local narration parts (no generation). The compiled short request must fit 5,000 bytes, including request fields."
                    : string.Empty);
        UpdateDocument();
        model.PropertyChanged += OnDocumentChanged;
        void OnDocumentChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.DocumentText), StringComparison.Ordinal)) UpdateDocument();
        }

        ComboBox setups = new() { PlaceholderText = "Saved voice setup (optional)" };
        Func<Task>? reloadVoices = null;
        AutomationProperties.SetName(setups, "Saved voice setup");
        setups.ItemsSource = preferences.State.Setups.Select(s => s.Name).ToArray();
        setups.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(setups);
        setups.SelectionChanged += async (_, _) =>
        {
            if (setups.SelectedItem is not string name) return;
            VoiceSetup? setup = preferences.State.Setups.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (setup is null) return;
            model.ApplySavedVoiceSetup(setup);
            try { preferences.Remember(setup); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { model.StatusMessage = "Voice loaded for this session, but its preference could not be saved."; return; }
            model.StatusMessage = "Saved voice chosen. Loading verified voices before cost review.";
            if (reloadVoices is not null) await reloadVoices().ConfigureAwait(true);
        };
        TextBox locale = NewTextBox("Language, for example en-US");
        locale.Text = model.VoiceLabLocaleFilter;
        TextBox search = NewTextBox("Voice search (optional)");
        search.Text = model.VoiceLabSearchText;
        TextBlock remembered = Text("Remembered voice: " + (model.PreferredVoiceName ?? "none chosen") + ". Loading checks that this voice is still available.");
        body.Children.Add(remembered);
        model.PropertyChanged += OnVoicePreferenceChanged;
        void OnVoicePreferenceChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.VoiceLabLocaleFilter), StringComparison.Ordinal)) locale.Text = model.VoiceLabLocaleFilter;
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.PreferredVoiceName), StringComparison.Ordinal))
                remembered.Text = "Remembered voice: " + (model.PreferredVoiceName ?? "none chosen") + ". Loading checks that this voice is still available.";
        }
        AddLanguagePicker(body, model, locale, dialog);
        StackPanel filters = AddSection(body, "Voice search (optional)", expanded: false);

        AddField(filters, "Search voices", search);
        reloadVoices = AddRefreshControl(body, model, locale, search, dialog, autoLoad: true);
        AddVoiceSelector(body, model, dialog);
        AddVoiceSampleLinks(body, model, dialog);

        TextBlock pricing = Text(model.PricingCatalogActiveSummary);
        body.Children.Add(pricing);
        model.PropertyChanged += OnPricingChanged;
        void OnPricingChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.PricingCatalogActiveSummary), StringComparison.Ordinal)) pricing.Text = model.PricingCatalogActiveSummary;
        }
        AddPricingActivationControls(AddSection(body, "Review pricing source if pricing is unavailable", expanded: false), model, dialog);

        TextBlock estimate = Text("Prepare the exact current request to see its cost. No provider generation occurs during review.");
        TextBlock nextStep = Text(string.Empty);
        Button review = NewButton("Review current text, voice and cost");
        CheckBox consent = NewConfirmation("I approve this exact request and its displayed maximum cost.");
        Button create = NewButton("Create MP3 — approve and send to Google");
        create.Classes.Add("primary");
        create.Name = "GuideGenerateButton";
        ShellViewModel.GoogleGenerationSpendReview? displayed = null;
        bool busy = false;
        bool reviewing = false;
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
            review.IsEnabled = !busy && !reviewing && !model.IsConnectedVoiceCatalogLoading && model.CanPrepareGoogleGenerationSpend
                && model.SelectedVoiceLabVoice is not null && !string.IsNullOrWhiteSpace(model.DocumentText);
            setups.IsEnabled = !busy && !reviewing && !model.IsConnectedVoiceCatalogLoading;
            create.IsEnabled = !busy && !model.IsConnectedVoiceCatalogLoading && !model.GenerateWithGoogleCommand.IsRunning
                && current is not null && consent.IsChecked == true && model.CanApproveGoogleGenerationSpend;
            consent.IsEnabled = current is not null && !busy;
            nextStep.Text = CreateMp3NextStep.Describe(busy, reviewing, !string.IsNullOrWhiteSpace(model.DocumentText),
                model.IsConnectedVoiceCatalogLoading, model.SelectedVoiceLabVoice is not null,
                model.CanPrepareGoogleGenerationSpend, current is not null, consent.IsChecked == true, model.CanApproveGoogleGenerationSpend);
        }
        review.Click += async (_, _) =>
        {
            if (reviewing) return;
            reviewing = true;
            Refresh();
            try { await model.PrepareGoogleGenerationSpendReviewAsync().ConfigureAwait(true); }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { model.StatusMessage = "Cost review could not finish. Refresh voices, check the pricing source and current text. For a long document, use Advanced tools to create local parts. No generation was started."; }
            finally { reviewing = false; Refresh(); }
        };
        consent.IsCheckedChanged += (_, _) => Refresh();
        model.PropertyChanged += OnReviewChanged;
        void OnReviewChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(ShellViewModel.PreparedGoogleGenerationSpendReview)
                or nameof(ShellViewModel.CanApproveGoogleGenerationSpend) or nameof(ShellViewModel.IsConnectedVoiceCatalogLoading)
                or nameof(ShellViewModel.CanPrepareGoogleGenerationSpend) or nameof(ShellViewModel.SelectedVoiceLabVoice)
                or nameof(ShellViewModel.DocumentText)) Refresh();
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
        StackPanel output = AddSection(body, "Listen, save and find audio", expanded: true);
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
        Button close = NewButton("Close Create MP3");
        close.Click += (_, _) => dialog.Close();
        body.Children.Add(nextStep);
        body.Children.Add(estimate);
        body.Children.Add(consent);
        body.Children.Add(create);
        AddStopWaitingControl(body, model, dialog);
        body.Children.Add(status);
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        ScrollViewer bodyScroll = new() { Content = body };
        layout.Children.Add(bodyScroll);
        StackPanel footer = new() { Spacing = 10, Margin = new Thickness(20, 8, 20, 16) };
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
        DialogBounds.Fit(dialog, owner);
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

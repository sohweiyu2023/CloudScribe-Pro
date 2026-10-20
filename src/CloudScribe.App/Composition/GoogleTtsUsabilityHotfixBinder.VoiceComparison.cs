using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    [SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Modal view composition and control event wiring stay together; authorization, sample boundaries and cache behavior are implemented and tested separately.")]
    private async Task OpenVoiceComparisonAsync(MainWindow owner, ShellViewModel model)
    {
        if (!model.CanPrepareGoogleGenerationSpend)
        {
            model.StatusMessage = "Wait for the current Google operation before opening voice samples.";
            return;
        }
        _sampleSession ??= new GoogleVoiceSampleSession(
            (spec, identity, token) =>
            {
                model.SelectedVoiceLabVoice = spec.Voice;
                model.VoiceLabLocaleFilter = spec.LanguageCode;
                return model.PrepareGoogleVoiceSampleAsync(spec.Text, identity, token);
            }, model.ApproveGoogleGenerationSpendAsync, model.GeneratePreparedGoogleVoiceSampleAsync,
            _acceptedMp3OutputService.VerifyAcceptedAsync, _acceptedMp3OutputService.PlayAsync,
            _acceptedMp3OutputService.ExportVerifiedAsync, model.CancelGoogleVoiceSampleReview);
        GoogleVoiceSampleSession session = _sampleSession;
        session.InvalidateReview();
        VoiceLabCatalogSelection? originalVoice = model.SelectedVoiceLabVoice;
        string? originalLocale = model.VoiceLabLocaleFilter;
        string? originalSearch = model.VoiceLabSearchText;
        bool useSelected = false;
        bool busy = false;
        bool rebuilding = false;

        Window dialog = new()
        {
            Title = "CloudScribe — Voices and short samples",
            Width = 980,
            Height = 780,
            MinWidth = 600,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#111C2B")),
        };
        StackPanel content = new() { Spacing = 8, Margin = new Thickness(18) };
        TextBlock Title(string value) => new() { Text = value, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        TextBlock Info(string value) => new() { Text = value, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(Title("Compare voices and try a short sample"));
        content.Children.Add(Info("Voice used for your existing AtR recordings: en-US-Standard-C. Current Studio voice: " + (originalVoice?.VoiceStableId ?? "not selected") + "."));
        content.Children.Add(Info("Selecting a voice sends nothing. Samples use only the box below; your document stays unchanged."));
        content.Children.Add(Info("USD list prices checked " + GoogleVoiceComparisonInformation.ReferenceChecked +
            " · authenticated catalog " + V222ControlSet.CatalogVersion + ". Prepare verifies the final request; estimates exclude free allowances, tax and FX."));

        WrapPanel links = new() { Orientation = Orientation.Horizontal };
        foreach ((string label, string url) in new[]
        {
            ("Official voice samples & catalog", GoogleVoiceComparisonInformation.CatalogUrl),
            ("Official pricing", GoogleVoiceComparisonInformation.PricingUrl),
        })
        {
            Button link = NewButton(label); link.Margin = new Thickness(0, 0, 8, 0);
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            links.Children.Add(link);
        }
        content.Children.Add(links);
        TextBox locale = NewTextBox("English locale, for example en-US"); locale.Text = "en-US";
        AutomationProperties.SetName(locale, "Voice comparison locale");
        TextBox filter = NewTextBox("Filter names or families");
        AutomationProperties.SetName(filter, "Filter comparison voices");
        Button refresh = NewButton("Refresh connected voices (no speech)");
        content.Children.Add(Info("Refresh your connected catalog to enable eligible samples. Reference-only entries can use the official demos."));
        Grid filters = new() { ColumnDefinitions = new ColumnDefinitions("120,*,Auto"), ColumnSpacing = 10 };
        filters.Children.Add(locale); Grid.SetColumn(filter, 1); filters.Children.Add(filter); Grid.SetColumn(refresh, 2); filters.Children.Add(refresh);
        content.Children.Add(filters);

        ListBox voices = new() { Height = 185 };
        AutomationProperties.SetName(voices, "Voice comparison list");
        voices.ItemTemplate = new FuncDataTemplate<ComparisonVoice>((row, _) => Info(row?.ListLabel ?? string.Empty));
        TextBlock details = Info(string.Empty); AutomationProperties.SetName(details, "Selected voice details");
        Grid comparison = new() { ColumnDefinitions = new ColumnDefinitions("380,*"), ColumnSpacing = 14 };
        comparison.Children.Add(voices); Grid.SetColumn(details, 1); comparison.Children.Add(details); content.Children.Add(comparison);
        TextBox sample = NewTextBox("Short sample text"); sample.Text = GoogleVoiceComparisonInformation.DefaultText;
        sample.AcceptsReturn = true; sample.TextWrapping = TextWrapping.Wrap; sample.MinHeight = 70; sample.MaxHeight = 130;
        AutomationProperties.SetName(sample, "Voice sample text"); content.Children.Add(Info("Short sample — 1 to 500 characters. MP3, normal speed, zero pitch and volume adjustment.")); content.Children.Add(sample);
        TextBlock estimates = Info(string.Empty); AutomationProperties.SetName(estimates, "Voice sample cost and character count"); content.Children.Add(estimates);
        TextBlock status = Info("No speech request has been sent. The official links offer prerecorded examples; generating your own sample may be billed.");
        AutomationProperties.SetName(status, "Voice sample status"); content.Children.Add(status);
        CheckBox consent = NewConfirmation("I approve this exact sample and its displayed USD request ceiling.");
        content.Children.Add(consent);
        Button prepare = NewButton("Prepare sample and show exact price");
        Button generate = NewButton("Generate sample"); generate.Classes.Add("primary");
        Button replay = NewButton("Replay matching sample — USD 0");
        Button export = NewButton("Export matching sample MP3");
        Button use = NewButton("Use selected voice in Studio");
        Button close = NewButton("Close voice comparison");
        WrapPanel actions = new() { Orientation = Orientation.Horizontal };
        foreach (Button button in new[] { prepare, generate, replay, export, use, close })
        { button.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(button); }
        content.Children.Add(actions);
        AddStopWaitingControl(content, model);
        content.Children.Add(Info("Verified replay/export is free. This-session cache matches the exact account, project, voice, language, text and MP3 settings; restarting clears it."));
        dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

        GoogleVoiceSampleSpec? Spec()
        {
            if (voices.SelectedItem is not ComparisonVoice { Selection: { } selected }) return null;
            string? language = selected.LanguageCodes?.FirstOrDefault(code => string.Equals(code, locale.Text?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? (selected.LanguageCodes is { Count: > 0 } codes ? codes[0] : null);
            if (language is null) return null;
            var spec = new GoogleVoiceSampleSpec(selected, language, sample.Text ?? string.Empty);
            try { spec.Validate(); return spec; } catch (InvalidOperationException) { return null; }
        }

        void Update()
        {
            ComparisonVoice? row = voices.SelectedItem as ComparisonVoice;
            var rate = row is null ? null : GoogleVoiceComparisonInformation.Rate(_comparisonPricing, row.Name);
            string text = sample.Text ?? string.Empty;
            string language = (row?.Selection?.LanguageCodes is { Count: > 0 } languages ? languages[0] : null)
                ?? (row?.Name is { Length: >= 5 } name ? name[..5] : "not available");
            string gender = row?.Selection?.SsmlGender ?? "not returned — no inference from voice letters";
            details.Text = row is null ? "Choose a voice." : row.Name + "\n" + GoogleVoiceComparisonInformation.LocaleLabel(language) +
                " · Provider gender label: " + gender + "\n" + GoogleVoiceComparisonInformation.Description(row.Name) + "\n" +
                (row.Selection is null ? "Reference only: not currently loaded from the connected catalog." :
                 GoogleVoiceComparisonInformation.SupportsSampleFamily(row.Name) ? "Connected catalog entry; generation still requires current independent checks." :
                 "Reference only in this sample flow: use the official samples; no generation support is assumed.") + "\n" +
                (rate?.PriceLabel ?? "No admitted character price; generation unavailable.");
            estimates.Text = text.Length.ToString(CultureInfo.InvariantCulture) + " / 500 sample characters. " +
                (rate is null ? "No verified character-rate estimate." :
                 "Sample list estimate USD " + rate.ListEstimate(text).ToString("0.000000", CultureInfo.InvariantCulture) +
                 "; conservative request ceiling USD " + (rate.ConservativeCents(text) / 100m).ToString("F2", CultureInfo.InvariantCulture) +
                 ". Current document (planning only, " + model.DocumentText.Length.ToString(CultureInfo.InvariantCulture) + " characters): USD " +
                 rate.ListEstimate(model.DocumentText).ToString("0.0000", CultureInfo.InvariantCulture) + ".");
            GoogleVoiceSampleSpec? spec = Spec();
            bool cached = spec is not null && session.HasCached(spec);
            prepare.IsEnabled = !busy && spec is not null && rate is not null;
            generate.IsEnabled = !busy && spec is not null && session.Review is not null && consent.IsChecked == true;
            consent.IsEnabled = !busy && session.Review is not null;
            replay.IsEnabled = !busy && cached; export.IsEnabled = !busy && cached && dialog.StorageProvider.CanSave;
            use.IsEnabled = !busy && spec is not null;
            close.IsEnabled = !busy; refresh.IsEnabled = !busy;
            locale.IsEnabled = !busy; filter.IsEnabled = !busy; sample.IsEnabled = !busy; voices.IsEnabled = !busy;
        }

        void Changed()
        {
            if (busy || rebuilding) return;
            session.InvalidateReview(); consent.IsChecked = false;
            status.Text = "Selection/text changed. No new request sent; prepare again to review this exact sample.";
            Update();
        }

        void Populate(string? prefer)
        {
            rebuilding = true;
            try
            {
                var connected = model.VoiceLabCatalogResults.Where(v => v.LanguageCodes?.Any(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)) == true)
                    .ToDictionary(v => v.VoiceStableId, StringComparer.Ordinal);
                IEnumerable<string> names = GoogleVoiceComparisonInformation.SuggestedVoices.Concat(connected.Keys).Distinct(StringComparer.Ordinal);
                string search = filter.Text?.Trim() ?? string.Empty;
                List<ComparisonVoice> rows = names.Where(n => n.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => GoogleVoiceComparisonInformation.SuggestedVoices.Contains(n, StringComparer.Ordinal) ? 0 : 1)
                    .ThenBy(n => n, StringComparer.Ordinal)
                    .Select(n => new ComparisonVoice(n, connected.GetValueOrDefault(n), GoogleVoiceComparisonInformation.Rate(_comparisonPricing, n))).ToList();
                voices.ItemsSource = rows;
                voices.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Name, prefer, StringComparison.Ordinal)) ?? rows.FirstOrDefault(r => string.Equals(r.Name, "en-US-Standard-C", StringComparison.Ordinal)) ?? rows.FirstOrDefault();
            }
            finally { rebuilding = false; }
            Changed();
        }

        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true; Update();
            try { await action().ConfigureAwait(true); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                consent.IsChecked = false;
                status.Text = "Action stopped safely; no automatic retry. Check the connected voice, current pricing and queue in Studio before preparing again. " +
                    (exception is InvalidOperationException && exception.Message.StartsWith("This sample", StringComparison.Ordinal) ? exception.Message : string.Empty);
            }
            finally { busy = false; Update(); }
        }

        voices.SelectionChanged += (_, _) => Changed();
        sample.TextChanged += (_, _) => Changed();
        filter.TextChanged += (_, _) => { if (!busy) Populate((voices.SelectedItem as ComparisonVoice)?.Name); };
        locale.TextChanged += (_, _) => Changed();
        consent.IsCheckedChanged += (_, _) => Update();
        refresh.Click += async (_, _) => await Run(async () =>
        {
            session.InvalidateReview(); consent.IsChecked = false;
            model.VoiceLabLocaleFilter = locale.Text?.Trim(); model.VoiceLabSearchText = null;
            await _bootstrapService.RefreshConfiguredAsync(CancellationToken.None).ConfigureAwait(true);
            await model.RefreshVoiceLabCatalogCommand.ExecuteAsync(null).ConfigureAwait(true);
            Populate((voices.SelectedItem as ComparisonVoice)?.Name);
            status.Text = "Connected voice catalog refreshed. No speech was generated. Check the selection and prepare the short sample.";
        }).ConfigureAwait(true);
        prepare.Click += async (_, _) => await Run(async () =>
        {
            GoogleVoiceSampleSpec spec = Spec() ?? throw new InvalidOperationException("Select a connected supported voice and 1 to 500 characters.");
            consent.IsChecked = false;
            bool cached = await session.PrepareAsync(spec).ConfigureAwait(true);
            status.Text = cached ? "Verified matching sample found in this session. Replay/export costs USD 0; no provider call was made." :
                "Exact sample request: " + FormatEstimate(session.Review!) + " ceiling. Voice: " + session.Review!.VoiceName +
                ". Review, check approval, then choose Generate sample. Nothing has been sent. Active pricing: " + session.Review.PricingProvenanceId[..Math.Min(16, session.Review.PricingProvenanceId.Length)] + ".";
        }).ConfigureAwait(true);
        generate.Click += async (_, _) => await Run(async () =>
        {
            GoogleVoiceSampleSpec spec = Spec() ?? throw new InvalidOperationException("Sample selection changed.");
            await session.GenerateAsync(spec, consent.IsChecked == true).ConfigureAwait(true);
            consent.IsChecked = false;
            status.Text = "Verified sample generated and cached in this session. Playback is manual; replay/export costs USD 0.";
        }).ConfigureAwait(true);
        replay.Click += async (_, _) => await Run(async () =>
        {
            await session.ReplayAsync(Spec() ?? throw new InvalidOperationException("No matching selection.")).ConfigureAwait(true);
            status.Text = "Opened the verified matching sample. No provider request or charge was incurred by replay.";
        }).ConfigureAwait(true);
        export.Click += async (_, _) => await Run(async () =>
        {
            GoogleVoiceSampleSpec spec = Spec() ?? throw new InvalidOperationException("No matching selection.");
            IStorageFile? destination = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export verified voice sample",
                SuggestedFileName = spec.Voice.VoiceStableId + "-sample.mp3",
                DefaultExtension = "mp3",
                FileTypeChoices = [Mp3FileType],
                ShowOverwritePrompt = true,
            }).ConfigureAwait(true);
            if (destination is null) return;
            if (!destination.Path.IsFile) throw new InvalidOperationException("Choose a local MP3 destination.");
            await session.ExportAsync(spec, destination.Path.LocalPath).ConfigureAwait(true);
            status.Text = "Verified sample exported without a provider request or new charge.";
        }).ConfigureAwait(true);
        use.Click += (_, _) =>
        {
            GoogleVoiceSampleSpec? spec = Spec(); if (spec is null) return;
            model.SelectedVoiceLabVoice = spec.Voice; model.VoiceLabLocaleFilter = spec.LanguageCode;
            model.VoiceLabSearchText = null; useSelected = true; dialog.Close();
            model.RememberSelectedVoiceSetup();
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        Populate(originalVoice?.VoiceStableId ?? "en-US-Standard-C");
        try { await dialog.ShowDialog(owner).ConfigureAwait(true); }
        finally
        {
            session.InvalidateReview();
            if (!useSelected)
            {
                model.SelectedVoiceLabVoice = originalVoice;
                model.VoiceLabLocaleFilter = originalLocale; model.VoiceLabSearchText = originalSearch;
            }
        }
    }

    private sealed record ComparisonVoice(string Name, VoiceLabCatalogSelection? Selection, GoogleVoiceComparisonInformation.CharacterRate? Rate)
    {
        public string ListLabel => Name + "  ·  " + (Rate?.PriceLabel ?? "price unavailable") +
            (Selection is null ? "  ·  reference only" : GoogleVoiceComparisonInformation.SupportsSampleFamily(Name)
                ? "  ·  connected / short-sample family" : "  ·  catalog entry / official samples only");
    }
}

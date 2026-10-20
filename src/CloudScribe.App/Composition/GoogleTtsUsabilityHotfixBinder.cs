using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>
/// Mounts the 1.0.1 Google TTS usability controls into the existing Studio inspector without
/// introducing an alternate provider-submit path. Credential onboarding ends at the persisted
/// catalog-evidence bootstrap; generation remains exclusively behind the existing Stage6 command.
/// </summary>
public sealed partial class GoogleTtsUsabilityHotfixBinder(
    GoogleTextToSpeechCatalogBootstrapService bootstrapService,
    BuiltInPricingCatalogBootstrapService builtInPricing,
    GoogleGenerationAcceptedMp3OutputService acceptedMp3OutputService,
    StudioPreferencesStore preferences,
    IProviderAccountStore accounts,
    AppPaths paths,
    LocalRequestStatusReader requestStatus)
{
    private static readonly string[] ProviderControlHostNames =
    [
        "ProviderControlPreview",
        "InspectorDrawerProviderControlPreview",
    ];

    private static readonly string[] Mp3OutputOptions = ["MP3 · preserve accepted provider bytes"];
    private static readonly FilePickerFileType Mp3FileType = new("MP3 audio")
    {
        Patterns = ["*.mp3"],
        MimeTypes = ["audio/mpeg"],
    };

    private readonly GoogleTextToSpeechCatalogBootstrapService _bootstrapService =
        bootstrapService ?? throw new ArgumentNullException(nameof(bootstrapService));
    private readonly BuiltInPricingCatalogBootstrapService _builtInPricing =
        builtInPricing ?? throw new ArgumentNullException(nameof(builtInPricing));
    private readonly GoogleGenerationAcceptedMp3OutputService _acceptedMp3OutputService =
        acceptedMp3OutputService ?? throw new ArgumentNullException(nameof(acceptedMp3OutputService));
    private readonly V222ControlSet _comparisonPricing = new(new StrictJsonObjectReader());
    private GoogleVoiceSampleSession? _sampleSession;

    public void Attach(MainWindow window, ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        if (TryMount(window, viewModel))
            return;

        window.Opened += HandleOpened;
        void HandleOpened(object? sender, EventArgs args)
        {
            window.Opened -= HandleOpened;
            _ = TryMount(window, viewModel);
        }
    }

    private bool TryMount(MainWindow window, ShellViewModel viewModel)
    {
        bool mounted = false;
        foreach (string hostName in ProviderControlHostNames)
        {
            StackPanel? host = window.FindControl<StackPanel>(hostName);
            if (host is null)
                continue;

            MountHost(window, host, viewModel);
            mounted = true;
        }

        if (mounted && window.FindControl<Button>("VoiceComparisonButton") is { } comparison)
        {
            comparison.Click += async (_, _) => await OpenVoiceComparisonAsync(window, viewModel).ConfigureAwait(true);
        }
        if (mounted && window.FindControl<Button>("WorkspaceSettingsButton") is { } settings)
            settings.Click += async (_, _) => await OpenWorkspaceSettingsAsync(window, viewModel).ConfigureAwait(true);
        if (mounted && window.FindControl<Button>("CreateMp3Button") is { } create)
            create.Click += async (_, _) => await OpenCreateMp3Async(window, viewModel).ConfigureAwait(true);
        if (mounted && window.FindControl<Button>("QuickStartHelpButton") is { } help)
            help.Click += async (_, _) => await OpenQuickStartAsync(window, viewModel).ConfigureAwait(true);
        if (mounted && preferences.IsFreshProfile && !preferences.State.QuickStartSeen)
        {
            if (window.IsVisible)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = OpenQuickStartObservedAsync(window, viewModel));
            else
                window.Opened += (_, _) => _ = OpenQuickStartObservedAsync(window, viewModel);
        }
        return mounted;
    }

    private void MountHost(MainWindow window, StackPanel host, ShellViewModel viewModel)
    {
        host.Children.Clear();
        host.Spacing = 14;
        AddIntro(host);
        TextBlock connectionStatus = NewDescription("Reading saved connection metadata...");
        host.Children.Add(connectionStatus);
        host.Children.Add(NewDescription("Write or import text, then choose Create MP3. Your document autosaves locally."));
        Button create = NewButton("Create MP3");
        create.Classes.Add("primary");
        create.Click += async (_, _) => await OpenCreateMp3Async(window, viewModel).ConfigureAwait(true);
        host.Children.Add(create);

        StackPanel advanced = AddSection(host, "Advanced tools and connection settings", expanded: false);
        AddLocalPartsControls(window, advanced, viewModel);
        Expander connectionSetup = new() { Header = "Connect or change Google account", IsExpanded = false };
        StackPanel connectionFields = new() { Spacing = 10 };
        connectionSetup.Content = connectionFields;
        AddOnboardingControls(connectionFields, viewModel);
        advanced.Children.Add(connectionSetup);
        Button changeConnection = NewButton("Change connection");
        changeConnection.IsVisible = false;
        changeConnection.Click += (_, _) => { connectionSetup.IsVisible = true; connectionSetup.IsExpanded = true; };
        advanced.Children.Add(changeConnection);
        _ = LoadConnectionPresentationAsync(connectionStatus, connectionSetup, changeConnection);
        AddCatalogControls(AddSection(advanced, "Voice catalog and filters", expanded: false), viewModel);
        AddPricingActivationControls(AddSection(advanced, "Pricing source", expanded: false), viewModel);
        AddOutputAndSpendControls(AddSection(advanced, "Detailed request review", expanded: false), viewModel);
        AddGenerateControls(advanced, viewModel);
        AddStopWaitingControl(advanced, viewModel);
        AddVerifiedOutputControls(window, AddSection(advanced, "Listen and export", expanded: false), viewModel);
        AddLiveStatus(host, viewModel);
    }
    private static void AddIntro(StackPanel host)
    {
        host.Children.Add(new TextBlock
        {
            Text = "Your voice studio",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Connect once, choose a voice, then review the price before generating audio. Your document stays local until you generate.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
    }

    private void AddOnboardingControls(StackPanel host, ShellViewModel viewModel)
    {
        host.Children.Add(NewDescription("Your service-account key is stored in Windows Credential Manager. This field clears after setup. Use catalog and synthesis URLs from the same Google API origin."));
        var controls = new OnboardingControls(
            NewTextBox("Account ID (new, local CloudScribe identity)"),
            NewTextBox("Display name"),
            NewTextBox("Credential reference ID (new vault target)"),
            NewTextBox("Google region identity (for example global)"),
            NewTextBox("Google voice catalog HTTPS endpoint"),
            NewTextBox("Google synthesis HTTPS endpoint"),
            NewTextBox("Paste service-account JSON (cleared after Configure)"),
            NewButton("Configure Google TTS securely"));
        controls.ServiceAccountJson.AcceptsReturn = true;
        controls.ServiceAccountJson.MinHeight = 86;
        controls.RegionId.Text = "global";
        controls.CatalogEndpoint.Text = "https://texttospeech.googleapis.com/v1/voices";
        controls.SynthesisEndpoint.Text = "https://texttospeech.googleapis.com/v1/text:synthesize";
        AddField(host, "Local account ID", controls.AccountId);
        AddField(host, "Account display name", controls.DisplayName);
        AddField(host, "Credential name", controls.CredentialReference);
        AddField(host, "Google region", controls.RegionId);
        AddField(host, "Voice catalog URL", controls.CatalogEndpoint);
        AddField(host, "Synthesis URL", controls.SynthesisEndpoint);
        AddField(host, "Service-account JSON", controls.ServiceAccountJson);
        controls.Configure.Click += async (_, _) => await ConfigureFreshAsync(viewModel, controls).ConfigureAwait(true);
        host.Children.Add(controls.Configure);
    }

    private async Task ConfigureFreshAsync(ShellViewModel viewModel, OnboardingControls controls)
    {
        if (!controls.Configure.IsEnabled)
            return;
        controls.Configure.IsEnabled = false;
        try
        {
            string account = Require(controls.AccountId.Text, "account ID");
            string name = Require(controls.DisplayName.Text, "display name");
            string credential = Require(controls.CredentialReference.Text, "credential reference ID");
            string region = Require(controls.RegionId.Text, "region identity");
            string json = RequireServiceAccountJson(controls.ServiceAccountJson.Text);
            Uri catalogEndpoint = RequireHttpsEndpoint(controls.CatalogEndpoint.Text, "voice catalog endpoint");
            Uri synthesisEndpoint = RequireHttpsEndpoint(controls.SynthesisEndpoint.Text, "synthesis endpoint");
            viewModel.StatusMessage = "Google TTS · authenticating service account and verifying real voice catalog";
            GoogleTextToSpeechCatalogBootstrapResult result = await _bootstrapService.ConfigureFreshAsync(
                account,
                name,
                credential,
                json.AsMemory(),
                catalogEndpoint,
                synthesisEndpoint,
                region,
                CancellationToken.None).ConfigureAwait(true);
            viewModel.StatusMessage = $"Google TTS configured · project {result.ProjectId} · {result.Voices.Count} observed voices";
            await viewModel.RefreshVoiceLabCatalogCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Google TTS configuration failed safely · {ex.Message}";
        }
        finally
        {
            controls.ServiceAccountJson.Text = string.Empty;
            controls.Configure.IsEnabled = true;
        }
    }

    private void AddCatalogControls(StackPanel host, ShellViewModel viewModel)
    {
        host.Children.Add(new TextBlock
        {
            Text = "Real voice catalog",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        TextBox locale = NewTextBox("Language / locale filter (for example en-US)");
        locale.Text = viewModel.VoiceLabLocaleFilter;
        TextBox search = NewTextBox("Voice search (optional)");
        search.Text = viewModel.VoiceLabSearchText;
        Observe(host, viewModel, (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.VoiceLabLocaleFilter), StringComparison.Ordinal))
                locale.Text = viewModel.VoiceLabLocaleFilter;
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.VoiceLabSearchText), StringComparison.Ordinal))
                search.Text = viewModel.VoiceLabSearchText;
        });
        host.Children.Add(NewDescription("Apply language and search filters with Refresh voices."));
        AddField(host, "Language or locale", locale);
        AddField(host, "Search voices", search);
        AddRefreshControl(host, viewModel, locale, search);
        AddVoiceSelector(host, viewModel);
    }

    private void AddRefreshControl(
        StackPanel host,
        ShellViewModel viewModel,
        TextBox locale,
        TextBox search,
        Window? lifetime = null)
    {
        Button refresh = NewButton("Refresh real Google voices");
        refresh.Click += async (_, _) =>
        {
            if (!refresh.IsEnabled)
                return;
            refresh.IsEnabled = false;
            try
            {
                viewModel.VoiceLabLocaleFilter = locale.Text;
                viewModel.VoiceLabSearchText = search.Text;
                await _bootstrapService.RefreshConfiguredAsync(CancellationToken.None).ConfigureAwait(true);
                await viewModel.RefreshVoiceLabCatalogCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                viewModel.StatusMessage = "Could not refresh voices. Check your Google connection and language filter, then try again.";
            }
            finally
            {
                refresh.IsEnabled = viewModel.CanRefreshVoiceLabCatalog;
            }
        };
        Observe(host, viewModel, (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.CanRefreshVoiceLabCatalog), StringComparison.Ordinal))
                refresh.IsEnabled = viewModel.CanRefreshVoiceLabCatalog;
        }, lifetime);
        refresh.IsEnabled = viewModel.CanRefreshVoiceLabCatalog;
        host.Children.Add(refresh);
    }

    private static void AddVoiceSelector(StackPanel host, ShellViewModel viewModel, Window? lifetime = null)
    {
        ComboBox voices = new() { PlaceholderText = "Select a verified Google voice" };
        AddField(host, "Voice", voices);
        VoiceLabCatalogSelection[] snapshot = [];
        bool rebuilding = false;
        void Rebuild()
        {
            rebuilding = true;
            try
            {
                snapshot = viewModel.VoiceLabCatalogResults.ToArray();
                voices.ItemsSource = snapshot.Select(item => item.VoiceStableId).ToArray();
                int index = Array.FindIndex(snapshot, item => SameVoice(item, viewModel.SelectedVoiceLabVoice));
                voices.SelectedIndex = index;
            }
            finally
            {
                rebuilding = false;
            }
        }
        NotifyCollectionChangedEventHandler catalogChanged = (_, _) => Rebuild();
        ((INotifyCollectionChanged)viewModel.VoiceLabCatalogResults).CollectionChanged += catalogChanged;
        if (lifetime is not null) lifetime.Closed += (_, _) =>
            ((INotifyCollectionChanged)viewModel.VoiceLabCatalogResults).CollectionChanged -= catalogChanged;
        Observe(host, viewModel, (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(ShellViewModel.SelectedVoiceLabVoice), StringComparison.Ordinal))
                Rebuild();
        }, lifetime);
        voices.SelectionChanged += (_, _) =>
        {
            if (!rebuilding && voices.SelectedIndex >= 0 && voices.SelectedIndex < snapshot.Length)
            {
                viewModel.SelectedVoiceLabVoice = snapshot[voices.SelectedIndex];
                viewModel.RememberSelectedVoiceSetup();
            }
        };
        Rebuild();
    }

    private static bool SameVoice(VoiceLabCatalogSelection left, VoiceLabCatalogSelection? right) =>
        right is not null &&
        string.Equals(left.ProviderStableId, right.ProviderStableId, StringComparison.Ordinal) &&
        string.Equals(left.AccountStableId, right.AccountStableId, StringComparison.Ordinal) &&
        string.Equals(left.ProjectStableId, right.ProjectStableId, StringComparison.Ordinal) &&
        string.Equals(left.VoiceStableId, right.VoiceStableId, StringComparison.Ordinal) &&
        string.Equals(left.CapabilityEvidenceId, right.CapabilityEvidenceId, StringComparison.Ordinal) &&
        string.Equals(left.VoiceFingerprint, right.VoiceFingerprint, StringComparison.Ordinal);

    private static void AddOutputAndSpendControls(StackPanel host, ShellViewModel viewModel)
    {
        host.Children.Add(new TextBlock { Text = "Output" });
        host.Children.Add(new ComboBox
        {
            ItemsSource = Mp3OutputOptions,
            SelectedIndex = 0,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Prepare your document and chosen voice to see a conservative provider-list subtotal, before tax, credits and currency conversion. Set a spending limit and approve this request before generating.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        TextBlock review = new()
        {
            Text = "No compiled Google request is awaiting spend review.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        Button prepareSpend = NewButton("Prepare exact request and show price");
        TextBox spendMaximum = NewTextBox("Authorized maximum minor units");
        CheckBox spendConfirmed = NewConfirmation("I approve this request and spending limit.");
        Button approveSpend = NewButton("Approve displayed spend");
        ShellViewModel.GoogleGenerationSpendReview? displayedReview = null;

        void RefreshReview()
        {
            ShellViewModel.GoogleGenerationSpendReview? current = viewModel.PreparedGoogleGenerationSpendReview;
            if (!ReferenceEquals(displayedReview, current))
            {
                displayedReview = current;
                spendConfirmed.IsChecked = false;
                spendMaximum.Text = current?.CurrentEstimateMinorUnits.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
            if (current is null)
            {
                review.Text = "No compiled Google request is awaiting spend review.";
                spendMaximum.IsEnabled = false;
                spendConfirmed.IsEnabled = false;
                approveSpend.IsEnabled = false;
                return;
            }

            string digest = current.CompiledPayloadSha256.Length > 16
                ? current.CompiledPayloadSha256[..16] + "…"
                : current.CompiledPayloadSha256;
            string approvalState = viewModel.PreparedGoogleGenerationSpendApproved
                ? "APPROVED for this exact compiled request"
                : "NOT YET APPROVED";
            review.Text =
                $"Exact compiled estimate: {FormatEstimate(current)} ({current.CurrentEstimateMinorUnits} minor units)\n" +
                $"Voice: {current.VoiceName}\n" +
                $"Pricing provenance: {current.PricingProvenanceId}\n" +
                $"Compiled payload SHA-256: {digest}\n" +
                $"Spend state: {approvalState}";
            bool canApprove = viewModel.CanApproveGoogleGenerationSpend;
            spendMaximum.IsEnabled = canApprove;
            spendConfirmed.IsEnabled = canApprove;
            approveSpend.IsEnabled = canApprove && spendConfirmed.IsChecked == true &&
                long.TryParse(spendMaximum.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long maximum) &&
                maximum >= current.CurrentEstimateMinorUnits;
        }

        prepareSpend.Click += async (_, _) =>
        {
            if (!prepareSpend.IsEnabled)
                return;
            prepareSpend.IsEnabled = false;
            try
            {
                await viewModel.PrepareGoogleGenerationSpendReviewAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                viewModel.StatusMessage = $"Google spend preparation failed safely · {ex.Message}";
            }
            finally
            {
                prepareSpend.IsEnabled = viewModel.CanPrepareGoogleGenerationSpend;
                RefreshReview();
            }
        };
        spendConfirmed.IsCheckedChanged += (_, _) => RefreshReview();
        spendMaximum.TextChanged += (_, _) => RefreshReview();
        approveSpend.Click += async (_, _) =>
        {
            await ApproveSpendAsync(
                viewModel,
                spendMaximum.Text,
                spendConfirmed.IsChecked == true).ConfigureAwait(true);
            RefreshReview();
        };
        Observe(host, viewModel, (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.PreparedGoogleGenerationSpendReview)
                or nameof(ShellViewModel.PreparedGoogleGenerationSpendApproved)
                or nameof(ShellViewModel.CanApproveGoogleGenerationSpend)
                or nameof(ShellViewModel.CanPrepareGoogleGenerationSpend))
            {
                prepareSpend.IsEnabled = viewModel.CanPrepareGoogleGenerationSpend;
                RefreshReview();
            }
        });

        host.Children.Add(prepareSpend);
        host.Children.Add(review);
        AddField(host, "Spending limit · minor units", spendMaximum);
        host.Children.Add(NewDescription("For a currency with two decimal places, 100 minor units = 1.00. The exact currency and estimate appear above."));
        host.Children.Add(spendConfirmed);
        host.Children.Add(approveSpend);
        prepareSpend.IsEnabled = viewModel.CanPrepareGoogleGenerationSpend;
        RefreshReview();
    }

    private void AddPricingActivationControls(StackPanel host, ShellViewModel viewModel, Window? lifetime = null)
    {
        host.Children.Add(new TextBlock
        {
            Text = "Pricing",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Check the provider’s current prices against the included catalog. Paste the pricing page you reviewed and confirm the rates match. Fetching a page records your review; it does not verify the rates automatically.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        TextBox currentSource = NewTextBox("Current provider pricing HTTPS source");
        currentSource.Text = GoogleVoiceComparisonInformation.PricingUrl;
        TextBlock active = NewDescription(viewModel.PricingCatalogActiveSummary);
        host.Children.Add(active);
        Observe(host, viewModel, (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ShellViewModel.PricingCatalogActiveSummary), StringComparison.Ordinal))
                active.Text = viewModel.PricingCatalogActiveSummary;
        }, lifetime);
        host.Children.Add(NewDescription("Active catalog selection survives restart. Refreshing account/capability access and approving an exact request are separate. Price review is never confirmed automatically."));
        TextBlock evidence = new()
        {
            Text = "No current pricing source has been observed in this session.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        CheckBox confirmed = NewConfirmation("I reviewed these prices and they match the included catalog rates.");
        currentSource.TextChanged += (_, _) => confirmed.IsChecked = false;
        Button activate = NewButton("Fetch current pricing evidence and activate");
        activate.Click += async (_, _) =>
        {
            if (!activate.IsEnabled)
                return;
            activate.IsEnabled = false;
            try
            {
                Uri source = RequireHttpsEndpoint(currentSource.Text, "pricing evidence source");
                BuiltInPricingCatalogBootstrapService.ActivationResult result = await _builtInPricing
                    .VerifyCurrentSourceAndActivateAsync(
                        source,
                        confirmed.IsChecked == true,
                        CancellationToken.None)
                    .ConfigureAwait(true);
                string shortHash = result.Observation.Sha256.Length > 16
                    ? result.Observation.Sha256[..16] + "…"
                    : result.Observation.Sha256;
                evidence.Text =
                    $"Observed current source: {result.Observation.SourceUri}\n" +
                    $"Observed UTC: {result.Observation.ObservedAtUtc:O}\n" +
                    $"Source SHA-256: {result.Observation.Sha256}\n" +
                    $"Bytes observed: {result.Observation.ByteCount}\n" +
                    "Trust: unsigned HTTPS observation + explicit user match confirmation\n" +
                    (result.ObservationRecorded ? "Review metadata recorded locally." : "Latest review metadata could not be recorded; this observation is visible in this session only.");
                viewModel.StatusMessage =
                    $"Pricing active · current source observed {result.Observation.ObservedAtUtc:O} · {shortHash} · manual unsigned activation recorded";
                await viewModel.RefreshLocalPricingHistoryAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                evidence.Text = $"Current pricing evidence not activated · {ex.Message}";
                viewModel.StatusMessage = $"Pricing activation failed safely · {ex.Message}";
            }
            finally
            {
                activate.IsEnabled = true;
            }
        };
        AddField(host, "Current provider pricing URL", currentSource);
        host.Children.Add(evidence);
        host.Children.Add(confirmed);
        host.Children.Add(activate);
    }

    private static async Task ApproveSpendAsync(
        ShellViewModel viewModel,
        string? maximumText,
        bool confirmed)
    {
        try
        {
            if (!long.TryParse(maximumText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long maximum) || maximum < 0)
                throw new InvalidOperationException("Enter a non-negative whole-number spend ceiling in minor units.");
            await viewModel.ApproveGoogleGenerationSpendAsync(
                maximum,
                confirmed,
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Google spend approval failed safely · {ex.Message}";
        }
    }

    private static string FormatEstimate(ShellViewModel.GoogleGenerationSpendReview review)
    {
        decimal divisor = 1m;
        for (int index = 0; index < review.Scale; index++)
            divisor *= 10m;
        decimal amount = review.CurrentEstimateMinorUnits / divisor;
        return $"{review.Currency} {amount.ToString($"F{review.Scale}", CultureInfo.InvariantCulture)}";
    }

    private static void AddGenerateControls(StackPanel host, ShellViewModel viewModel)
    {
        Button generate = new()
        {
            Content = "Generate with Google",
            Command = viewModel.GenerateWithGoogleCommand,
        };
        generate.Classes.Add("primary");
        generate.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        host.Children.Add(generate);
        host.Children.Add(new TextBlock
        {
            Text = "Generating sends the approved text to Google and may incur charges. Each new request needs a fresh price review and approval.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
    }

    private void AddVerifiedOutputControls(MainWindow window, StackPanel host, ShellViewModel viewModel, Window? lifetime = null)
    {
        TextBlock outputPath = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Button play = NewButton("Play verified MP3");
        Button export = NewButton("Export verified MP3…");
        play.Command = viewModel.PlayLastGeneratedGoogleMp3Command;
        export.Click += async (_, _) => await ExportVerifiedAsync(window, viewModel).ConfigureAwait(true);

        void Refresh()
        {
            outputPath.Text = string.IsNullOrWhiteSpace(viewModel.LastGeneratedGoogleMp3Path)
                ? "Your verified audio will appear here after generation."
                : $"Verified MP3 · {viewModel.LastGeneratedGoogleMp3Path}";
            play.IsEnabled = viewModel.CanPlayLastGeneratedGoogleMp3;
            export.IsEnabled = viewModel.CanPlayLastGeneratedGoogleMp3 && window.StorageProvider.CanSave;
        }
        Observe(host, viewModel, (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.LastGeneratedGoogleMp3Path)
                or nameof(ShellViewModel.CanPlayLastGeneratedGoogleMp3))
                Refresh();
        }, lifetime);
        Refresh();
        host.Children.Add(outputPath);
        host.Children.Add(play);
        host.Children.Add(export);
    }

    private async Task ExportVerifiedAsync(MainWindow window, ShellViewModel viewModel)
    {
        try
        {
            string sourcePath = viewModel.LastGeneratedGoogleMp3Path
                ?? throw new InvalidOperationException("No verified Google MP3 is available to export.");
            if (!window.StorageProvider.CanSave)
                throw new InvalidOperationException("This Windows storage provider cannot choose an MP3 export destination.");

            IStorageFile? destination = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export verified Google MP3",
                SuggestedFileName = Path.GetFileName(sourcePath),
                DefaultExtension = "mp3",
                FileTypeChoices = [Mp3FileType],
                ShowOverwritePrompt = true,
            }).ConfigureAwait(true);
            if (destination is null)
                return;
            if (!destination.Path.IsFile)
                throw new InvalidOperationException("The selected MP3 export destination is not a local Windows file.");

            viewModel.StatusMessage = "Google generation · exporting verified accepted MP3 bytes";
            string exportedPath = await _acceptedMp3OutputService.ExportVerifiedAsync(
                sourcePath,
                destination.Path.LocalPath,
                CancellationToken.None).ConfigureAwait(true);
            viewModel.StatusMessage = $"Google generation · verified MP3 exported · {exportedPath}";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Google MP3 export failed safely · {ex.Message}";
        }
    }

    private static TextBox NewTextBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
    };

    private static Uri RequireHttpsEndpoint(string? value, string label)
    {
        string endpointText = Require(value, label);
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Google TTS {label} must be an absolute HTTPS URI.");
        }
        return endpoint;
    }

    private static string RequireServiceAccountJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Google TTS service-account JSON is required.");
        if (value.Contains('\0'))
            throw new InvalidOperationException("Google TTS service-account JSON contains a forbidden NUL character.");
        return value.Trim();
    }

    private static string Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Google TTS {label} is required.");
        string result = value.Trim();
        if (result.Contains('\r') || result.Contains('\n') || result.Contains('\0'))
            throw new InvalidOperationException($"Google TTS {label} contains forbidden control characters.");
        return result;
    }

    private sealed record OnboardingControls(
        TextBox AccountId,
        TextBox DisplayName,
        TextBox CredentialReference,
        TextBox RegionId,
        TextBox CatalogEndpoint,
        TextBox SynthesisEndpoint,
        TextBox ServiceAccountJson,
        Button Configure);
}

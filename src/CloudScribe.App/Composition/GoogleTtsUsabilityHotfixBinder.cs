using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Mounts the 1.0.1 Google TTS usability controls into the existing Studio inspector without
/// introducing an alternate provider-submit path. Credential onboarding ends at the persisted
/// catalog-evidence bootstrap; generation remains exclusively behind the existing Stage6 command.
/// </summary>
public sealed class GoogleTtsUsabilityHotfixBinder(
    GoogleTextToSpeechCatalogBootstrapService bootstrapService)
{
    private readonly GoogleTextToSpeechCatalogBootstrapService _bootstrapService =
        bootstrapService ?? throw new ArgumentNullException(nameof(bootstrapService));

    public void Attach(MainWindow window, ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        if (!TryMount(window, viewModel))
        {
            window.Opened += HandleOpened;
        }

        void HandleOpened(object? sender, EventArgs args)
        {
            window.Opened -= HandleOpened;
            _ = TryMount(window, viewModel);
        }
    }

    private bool TryMount(MainWindow window, ShellViewModel viewModel)
    {
        StackPanel? host = window.FindControl<StackPanel>("InspectorDrawerProviderControlPreview");
        if (host is null)
            return false;

        host.Children.Clear();
        host.Spacing = 8;

        host.Children.Add(new TextBlock
        {
            Text = "Google Cloud TTS · 1.0.1",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Paste a service-account JSON only for this onboarding operation. CloudScribe imports it into the Windows credential vault, clears this field, obtains short-lived OAuth, and persists catalog trust only after a real authenticated voice-list response succeeds.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        TextBox accountId = NewTextBox("Account ID (new, local CloudScribe identity)");
        TextBox displayName = NewTextBox("Display name");
        TextBox credentialReference = NewTextBox("Credential reference ID (new vault target)");
        TextBox catalogEndpoint = NewTextBox("Google voice catalog HTTPS endpoint");
        TextBox serviceAccountJson = NewTextBox("Paste service-account JSON (cleared after Configure)");
        serviceAccountJson.AcceptsReturn = true;
        serviceAccountJson.MinHeight = 86;

        host.Children.Add(accountId);
        host.Children.Add(displayName);
        host.Children.Add(credentialReference);
        host.Children.Add(catalogEndpoint);
        host.Children.Add(serviceAccountJson);

        Button configure = new() { Content = "Configure Google TTS securely" };
        configure.Click += async (_, _) =>
        {
            if (configure.IsEnabled == false)
                return;

            configure.IsEnabled = false;
            try
            {
                string account = Require(accountId.Text, "account ID");
                string name = Require(displayName.Text, "display name");
                string credential = Require(credentialReference.Text, "credential reference ID");
                string json = Require(serviceAccountJson.Text, "service-account JSON");
                if (!Uri.TryCreate(catalogEndpoint.Text?.Trim(), UriKind.Absolute, out Uri? endpoint)
                    || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Google voice catalog endpoint must be an absolute HTTPS URI.");
                }

                viewModel.StatusMessage = "Google TTS · authenticating service account and verifying real voice catalog";
                GoogleTextToSpeechCatalogBootstrapResult result = await _bootstrapService.ConfigureFreshAsync(
                    account,
                    name,
                    credential,
                    json.AsMemory(),
                    endpoint,
                    CancellationToken.None).ConfigureAwait(true);

                viewModel.StatusMessage = $"Google TTS configured · project {result.ProjectId} · {result.Voices.Count} observed voices";
                viewModel.RefreshVoiceLabCatalogCommand.Execute(null);
            }
            catch (Exception ex)
            {
                viewModel.StatusMessage = $"Google TTS configuration failed safely · {ex.Message}";
            }
            finally
            {
                serviceAccountJson.Text = string.Empty;
                configure.IsEnabled = true;
            }
        };
        host.Children.Add(configure);

        host.Children.Add(new TextBlock
        {
            Text = "Real voice catalog",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });

        TextBox locale = NewTextBox("Language / locale filter (for example en-US)");
        locale.Text = viewModel.VoiceLabLocaleFilter;
        locale.LostFocus += (_, _) => viewModel.VoiceLabLocaleFilter = locale.Text;
        host.Children.Add(locale);

        TextBox search = NewTextBox("Voice search (optional)");
        search.Text = viewModel.VoiceLabSearchText;
        search.LostFocus += (_, _) => viewModel.VoiceLabSearchText = search.Text;
        host.Children.Add(search);

        Button refresh = new() { Content = "Refresh real Google voices" };
        refresh.Click += (_, _) =>
        {
            viewModel.VoiceLabLocaleFilter = locale.Text;
            viewModel.VoiceLabSearchText = search.Text;
            viewModel.RefreshVoiceLabCatalogCommand.Execute(null);
        };
        host.Children.Add(refresh);

        ComboBox voices = new() { PlaceholderText = "Select a verified Google voice" };
        host.Children.Add(voices);

        void RebuildVoiceItems()
        {
            string? selectedId = viewModel.SelectedVoiceLabVoice?.VoiceStableId;
            VoiceLabCatalogSelection[] snapshot = viewModel.VoiceLabCatalogResults.ToArray();
            voices.ItemsSource = snapshot.Select(item => item.VoiceStableId).ToArray();
            int index = selectedId is null
                ? -1
                : Array.FindIndex(snapshot, item => string.Equals(item.VoiceStableId, selectedId, StringComparison.Ordinal));
            if (index >= 0)
                voices.SelectedIndex = index;
        }

        ((INotifyCollectionChanged)viewModel.VoiceLabCatalogResults).CollectionChanged += (_, _) => RebuildVoiceItems();
        voices.SelectionChanged += (_, _) =>
        {
            int index = voices.SelectedIndex;
            if (index >= 0 && index < viewModel.VoiceLabCatalogResults.Count)
                viewModel.SelectedVoiceLabVoice = viewModel.VoiceLabCatalogResults[index];
        };
        RebuildVoiceItems();

        host.Children.Add(new TextBlock { Text = "Output" });
        ComboBox output = new()
        {
            ItemsSource = new[] { "MP3 · preserve accepted provider bytes" },
            SelectedIndex = 0,
        };
        host.Children.Add(output);

        host.Children.Add(new TextBlock
        {
            Text = "Spend approval is bound to the exact compiled request. Enter the maximum in the pricing currency's minor units and explicitly confirm before generation.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        TextBox spendMaximum = NewTextBox("Authorized maximum minor units");
        CheckBox spendConfirmed = new() { Content = "I explicitly approve this exact compiled spend ceiling" };
        Button approveSpend = new() { Content = "Compile and approve spend" };
        approveSpend.Click += async (_, _) =>
        {
            try
            {
                if (!long.TryParse(spendMaximum.Text, out long maximum) || maximum < 0)
                    throw new InvalidOperationException("Enter a non-negative whole-number spend ceiling in minor units.");
                bool confirmed = spendConfirmed.IsChecked == true;
                await viewModel.ApproveGoogleGenerationSpendAsync(maximum, confirmed, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                viewModel.StatusMessage = $"Google spend approval failed safely · {ex.Message}";
            }
        };
        host.Children.Add(spendMaximum);
        host.Children.Add(spendConfirmed);
        host.Children.Add(approveSpend);

        Button generate = new() { Content = "Generate with Google" };
        generate.Click += (_, _) => viewModel.GenerateWithGoogleCommand.Execute(null);
        host.Children.Add(generate);
        host.Children.Add(new TextBlock
        {
            Text = "Generate uses the existing fail-closed Stage6 authorization, persisted queue, guarded executor and current pricing evidence. This panel never submits directly to Google.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        return true;
    }

    private static TextBox NewTextBox(string watermark) => new()
    {
        Watermark = watermark,
    };

    private static string Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Google TTS {label} is required.");
        string result = value.Trim();
        if (result.Contains('\r') || result.Contains('\n') || result.Contains('\0'))
            throw new InvalidOperationException($"Google TTS {label} contains forbidden control characters.");
        return result;
    }
}

using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Controls;
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
        StackPanel? host = window.FindControl<StackPanel>("InspectorDrawerProviderControlPreview");
        if (host is null)
            return false;

        host.Children.Clear();
        host.Spacing = 8;
        AddIntro(host);
        AddOnboardingControls(host, viewModel);
        AddCatalogControls(host, viewModel);
        AddOutputAndSpendControls(host, viewModel);
        AddGenerateControls(host, viewModel);
        return true;
    }

    private static void AddIntro(StackPanel host)
    {
        host.Children.Add(new TextBlock
        {
            Text = "Google Cloud TTS · 1.0.1",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Paste service-account JSON only for onboarding. CloudScribe imports it into the Windows credential vault, clears the field, obtains short-lived OAuth, and persists catalog trust only after a real authenticated voice-list response succeeds.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
    }

    private void AddOnboardingControls(StackPanel host, ShellViewModel viewModel)
    {
        var controls = new OnboardingControls(
            NewTextBox("Account ID (new, local CloudScribe identity)"),
            NewTextBox("Display name"),
            NewTextBox("Credential reference ID (new vault target)"),
            NewTextBox("Google voice catalog HTTPS endpoint"),
            NewTextBox("Paste service-account JSON (cleared after Configure)"),
            new Button { Content = "Configure Google TTS securely" });
        controls.ServiceAccountJson.AcceptsReturn = true;
        controls.ServiceAccountJson.MinHeight = 86;
        host.Children.Add(controls.AccountId);
        host.Children.Add(controls.DisplayName);
        host.Children.Add(controls.CredentialReference);
        host.Children.Add(controls.CatalogEndpoint);
        host.Children.Add(controls.ServiceAccountJson);
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
            string json = Require(controls.ServiceAccountJson.Text, "service-account JSON");
            Uri endpoint = RequireHttpsEndpoint(controls.CatalogEndpoint.Text);
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
            controls.ServiceAccountJson.Text = string.Empty;
            controls.Configure.IsEnabled = true;
        }
    }

    private static void AddCatalogControls(StackPanel host, ShellViewModel viewModel)
    {
        host.Children.Add(new TextBlock
        {
            Text = "Real voice catalog",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        TextBox locale = NewTextBox("Language / locale filter (for example en-US)");
        locale.Text = viewModel.VoiceLabLocaleFilter;
        locale.LostFocus += (_, _) => viewModel.VoiceLabLocaleFilter = locale.Text;
        TextBox search = NewTextBox("Voice search (optional)");
        search.Text = viewModel.VoiceLabSearchText;
        search.LostFocus += (_, _) => viewModel.VoiceLabSearchText = search.Text;
        host.Children.Add(locale);
        host.Children.Add(search);
        AddRefreshControl(host, viewModel, locale, search);
        AddVoiceSelector(host, viewModel);
    }

    private static void AddRefreshControl(
        StackPanel host,
        ShellViewModel viewModel,
        TextBox locale,
        TextBox search)
    {
        Button refresh = new() { Content = "Refresh real Google voices" };
        refresh.Click += (_, _) =>
        {
            viewModel.VoiceLabLocaleFilter = locale.Text;
            viewModel.VoiceLabSearchText = search.Text;
            viewModel.RefreshVoiceLabCatalogCommand.Execute(null);
        };
        host.Children.Add(refresh);
    }

    private static void AddVoiceSelector(StackPanel host, ShellViewModel viewModel)
    {
        ComboBox voices = new() { PlaceholderText = "Select a verified Google voice" };
        host.Children.Add(voices);
        void Rebuild()
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
        ((INotifyCollectionChanged)viewModel.VoiceLabCatalogResults).CollectionChanged += (_, _) => Rebuild();
        voices.SelectionChanged += (_, _) => SelectVoice(viewModel, voices);
        Rebuild();
    }

    private static void SelectVoice(ShellViewModel viewModel, ComboBox voices)
    {
        int index = voices.SelectedIndex;
        if (index >= 0 && index < viewModel.VoiceLabCatalogResults.Count)
            viewModel.SelectedVoiceLabVoice = viewModel.VoiceLabCatalogResults[index];
    }

    private static void AddOutputAndSpendControls(StackPanel host, ShellViewModel viewModel)
    {
        host.Children.Add(new TextBlock { Text = "Output" });
        host.Children.Add(new ComboBox
        {
            ItemsSource = new[] { "MP3 · preserve accepted provider bytes" },
            SelectedIndex = 0,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Spend approval is bound to the exact compiled request. Enter the maximum in the pricing currency's minor units and explicitly confirm before generation.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        TextBox spendMaximum = NewTextBox("Authorized maximum minor units");
        CheckBox spendConfirmed = new() { Content = "I explicitly approve this exact compiled spend ceiling" };
        Button approveSpend = new() { Content = "Compile and approve spend" };
        approveSpend.Click += async (_, _) => await ApproveSpendAsync(
            viewModel,
            spendMaximum.Text,
            spendConfirmed.IsChecked == true).ConfigureAwait(true);
        host.Children.Add(spendMaximum);
        host.Children.Add(spendConfirmed);
        host.Children.Add(approveSpend);
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

    private static void AddGenerateControls(StackPanel host, ShellViewModel viewModel)
    {
        Button generate = new() { Content = "Generate with Google" };
        generate.Click += (_, _) => viewModel.GenerateWithGoogleCommand.Execute(null);
        host.Children.Add(generate);
        host.Children.Add(new TextBlock
        {
            Text = "Generate uses the existing fail-closed Stage6 authorization, persisted queue, guarded executor and current pricing evidence. This panel never submits directly to Google.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
    }

    private static TextBox NewTextBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
    };

    private static Uri RequireHttpsEndpoint(string? value)
    {
        string endpointText = Require(value, "voice catalog endpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Google voice catalog endpoint must be an absolute HTTPS URI.");
        }
        return endpoint;
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
        TextBox CatalogEndpoint,
        TextBox ServiceAccountJson,
        Button Configure);
}

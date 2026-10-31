using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static void AddLanguagePicker(StackPanel host, ShellViewModel model, TextBox code, Window lifetime)
    {
        TextBox search = NewTextBox("Find a language, for example Chinese or French");
        AutomationProperties.SetName(search, "Find a language by name or code");
        ComboBox choices = new() { PlaceholderText = "Choose a language", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choices, "Language selection");
        choices.ItemTemplate = new FuncDataTemplate<VoiceLanguageChoice>((item, _) => NewDescription(item?.Label ?? string.Empty));
        AddField(host, "Language", choices);
        host.Children.Add(search);
        TextBlock state = NewDescription(string.Empty);
        host.Children.Add(state);
        Avalonia.Threading.DispatcherTimer freshness = new() { Interval = TimeSpan.FromSeconds(1) };
        freshness.Tick += (_, _) => model.RefreshVoiceLanguageCatalogFreshness();
        lifetime.Closed += (_, _) => freshness.Stop();
        freshness.Start();
        bool rebuilding = false;
        void Rebuild()
        {
            rebuilding = true;
            try
            {
                var items = VoiceLanguageChoices.Build(model.VoiceLanguageCodes, model.VoiceLabLocaleFilter,
                    search.Text, model.HasCurrentVoiceLanguageCatalog);
                choices.ItemsSource = items;
                choices.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Code, model.VoiceLabLocaleFilter, StringComparison.Ordinal));
                choices.IsEnabled = !model.IsConnectedVoiceCatalogLoading;
                code.Text = model.VoiceLabLocaleFilter;
                state.Text = (model.HasCurrentVoiceLanguageCatalog
                    ? "Languages come from the verified Google catalog. "
                    : "Language availability is not currently verified. Refresh the connection to load available languages. ")
                    + "Selected: " + (model.VoiceLabLocaleFilter is { } selected ? LanguageDisplayNames.WithCode(selected) : "all languages")
                    + ". Searching this list is local. Choose a language, then refresh voices to apply it. Saved voices are never replaced automatically.";
            }
            finally { rebuilding = false; }
        }
        search.TextChanged += (_, _) => Rebuild();
        choices.SelectionChanged += (_, _) =>
        {
            if (rebuilding || choices.SelectedItem is not VoiceLanguageChoice selected) return;
            model.VoiceLabLocaleFilter = selected.Code;
            code.Text = selected.Code;
        };
        Observe(host, model, (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.VoiceLanguageCodes) or nameof(ShellViewModel.HasCurrentVoiceLanguageCatalog)
                or nameof(ShellViewModel.VoiceLabLocaleFilter) or nameof(ShellViewModel.IsConnectedVoiceCatalogLoading)) Rebuild();
        }, lifetime);
        Rebuild();
    }
}

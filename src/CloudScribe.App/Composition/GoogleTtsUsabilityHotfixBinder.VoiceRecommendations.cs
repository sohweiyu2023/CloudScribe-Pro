using System.Collections.Specialized;
using Avalonia.Automation;
using Avalonia.Controls;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private void AddVoiceRecommendations(StackPanel host, ShellViewModel model, Window? lifetime)
    {
        ComboBox policy = new() { ItemsSource = new[] { "Balanced language default", "Premium Chirp 3 HD (explicit choice)" }, SelectedIndex = 0 };
        AutomationProperties.SetName(policy, "Voice recommendation policy");
        AddField(host, "Recommended voice policy", policy);
        TextBlock details = NewDescription(string.Empty);
        host.Children.Add(details);
        Button use = NewButton("Use recommended voice");
        host.Children.Add(use);
        bool updating = false;
        VoiceLabCatalogSelection? recommendation = null;
        void Update()
        {
            if (updating) return;
            updating = true;
            try
            {
                bool hd = policy.SelectedIndex == 1;
                recommendation = model.IsConnectedVoiceCatalogLoading ? null : GoogleVoiceRecommendationPolicy.Recommend(
                    model.VoiceLabCatalogResults, model.VoiceLabLocaleFilter, hd);
                var price = recommendation is null ? null : GoogleVoiceRecommendationPolicy.ReferencePrice(_comparisonPricing, recommendation.VoiceStableId);
                use.IsEnabled = recommendation is not null && price?.IsUsable(DateTimeOffset.UtcNow) == true;
                details.Text = recommendation is null
                    ? "Set an exact language (for example en-US or cmn-CN), then load verified voices. No compatible recommendation is available for this policy; use the manual voice list."
                    : "Language match: " + LanguageDisplayNames.WithCode(model.VoiceLabLocaleFilter!) + ". Suggested: " + recommendation.VoiceStableId
                        + " (" + GoogleVoiceComparisonInformation.Family(recommendation.VoiceStableId) + "). "
                        + (price?.Describe(DateTimeOffset.UtcNow) ?? "Reference price unavailable; no price is guessed.")
                        + (hd ? " Premium alternative: plain text and default controls only; no SSML, pitch or volume adjustments are offered."
                              : " Policy: English prefers Neural2; other languages prefer WaveNet, then compatible Neural2/Standard. Chinese has no Neural2; WaveNet is the lower-cost alternative.")
                        + " Saved/manual voices are preserved. Choosing a suggestion does not generate audio.";
                if (GoogleVoiceRecommendationPolicy.MayApplyDefault(recommendation, model.SelectedVoiceLabVoice,
                    model.PreferredVoiceName, model.VoiceLabLocaleFilter, hd, price?.IsUsable(DateTimeOffset.UtcNow) == true))
                    model.SelectedVoiceLabVoice = recommendation;
            }
            finally { updating = false; }
        }
        policy.SelectionChanged += (_, _) => Update();
        use.Click += (_, _) =>
        {
            // Re-evaluate current catalog/locale/price at the explicit click, never use an old recommendation.
            Update();
            if (!use.IsEnabled || recommendation is null) return;
            model.SelectedVoiceLabVoice = recommendation;
            model.RememberSelectedVoiceSetup();
        };
        NotifyCollectionChangedEventHandler changed = (_, _) => Update();
        ((INotifyCollectionChanged)model.VoiceLabCatalogResults).CollectionChanged += changed;
        if (lifetime is not null) lifetime.Closed += (_, _) =>
            ((INotifyCollectionChanged)model.VoiceLabCatalogResults).CollectionChanged -= changed;
        Observe(host, model, (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.SelectedVoiceLabVoice) or nameof(ShellViewModel.IsConnectedVoiceCatalogLoading)
                or nameof(ShellViewModel.VoiceLabLocaleFilter) or nameof(ShellViewModel.PreferredVoiceName)) Update();
        }, lifetime);
        Update();
    }
}

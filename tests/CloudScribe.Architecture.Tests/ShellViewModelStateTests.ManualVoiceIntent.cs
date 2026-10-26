using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualVoiceIntentSurvivesRefreshWithoutDurablePreferences(bool corruptPreferences)
    {
        string? path = corruptPreferences ? Path.GetTempFileName() : null;
        try
        {
            using ShellViewModel model = CreateViewModel();
            if (path is not null)
            {
                await File.WriteAllTextAsync(path, "synthetic invalid preferences", TestContext.Current.CancellationToken);
                model.ConfigureSavedVoiceSetups(new StudioPreferencesStore(path));
            }
            var manual = VoiceSelection() with { VoiceStableId = "en-US-Standard-D", ProviderStableId = GoogleGenerationProvider.StableProviderId, LanguageCodes = ["en-US"] };
            var freshManual = manual with { CapabilityEvidenceId = "refreshed-evidence", VoiceFingerprint = "refreshed-fingerprint" };
            var neural = freshManual with { VoiceStableId = "en-US-Neural2-C" };
            model.VoiceLabLocaleFilter = "en-US";
            model.SelectedVoiceLabVoice = manual;
            model.RememberSelectedVoiceSetup();
            Assert.Equal(manual.VoiceStableId, model.PreferredVoiceName);
            if (path is not null)
                Assert.Equal("synthetic invalid preferences", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

            int refreshes = 0;
            model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
                Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>(++refreshes == 1 ? [freshManual, neural] : [neural])),
                _ => Task.FromResult(CatalogState(accountAuthorized: true) with
                { Query = new VoiceLabCatalogQuery(GoogleGenerationProvider.StableProviderId, "account", "project", "en-US", null, false) }));
            // The connected-voice loader clears stale selection before refreshing; preserve intent, not old evidence.
            model.SelectedVoiceLabVoice = null;
            await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
            Assert.True(model.VoiceLabCatalogLastRefreshSucceeded);
            Assert.Same(freshManual, model.SelectedVoiceLabVoice);
            Assert.Same(neural, GoogleVoiceRecommendationPolicy.Recommend(model.VoiceLabCatalogResults, "en-US"));
            Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(neural, model.SelectedVoiceLabVoice,
                model.PreferredVoiceName, "en-US", false, true));

            model.SelectedVoiceLabVoice = null;
            await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
            Assert.Null(model.SelectedVoiceLabVoice);
            Assert.Equal(manual.VoiceStableId, model.PreferredVoiceName);
            Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(neural, null, model.PreferredVoiceName, "en-US", false, true));
            Assert.Null(model.PreparedGoogleGenerationSpendReview);
            Assert.False(model.GenerateWithGoogleCommand.IsRunning);
        }
        finally { if (path is not null) File.Delete(path); }
    }
}

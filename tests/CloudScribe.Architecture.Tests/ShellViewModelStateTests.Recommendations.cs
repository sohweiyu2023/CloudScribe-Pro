using CloudScribe.App.Composition;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public void RecommendationsDoNotSelectPersistPrepareOrSynthesize()
    {
        using var model = CreateViewModel();
        model.VoiceLabCatalogResults.Add(new VoiceLabCatalogSelection("en-US-Neural2-C",
            GoogleGenerationProvider.StableProviderId, "account", "project", "evidence", "fingerprint", true, true, true, ["en-US"]));
        var recommendation = GoogleVoiceRecommendationPolicy.Recommend(model.VoiceLabCatalogResults, "en-US");
        Assert.NotNull(recommendation);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Null(model.PreferredVoiceName);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.GenerateWithGoogleCommand.IsRunning);
    }
}

using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("voice", true)]
    [InlineData("different-voice", false)]
    public async Task SavedPreferenceNeedsRefreshedExactCatalogMatch(string preferred, bool matches)
    {
        string file = Path.Combine(Path.GetTempPath(), "cloudscribe-setup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new StudioPreferencesStore(file); store.Remember(new("Last", "en-US", preferred));
            using ShellViewModel model = CreateViewModel(); model.ConfigureSavedVoiceSetups(store);
            Assert.Null(model.SelectedVoiceLabVoice); Assert.False(model.CanGenerateWithGoogle);
            VoiceLabCatalogSelection selected = VoiceSelection();
            var catalog = new VoiceLabCatalogQueryService((_, _) => Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([selected]));
            model.ConfigureStage7VoiceLabCatalog(catalog, _ => Task.FromResult(CatalogState(accountAuthorized: true)));
            await model.RefreshVoiceLabCatalogCommand.ExecuteAsync(null);
            Assert.Equal(matches, model.SelectedVoiceLabVoice is not null);
            Assert.False(model.CanGenerateWithGoogle);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("text")]
    [InlineData("voice")]
    [InlineData("locale")]
    public async Task ChangingRequestClearsSpendAndNeverInvokesRuntime(string field)
    {
        using ShellViewModel model = CreateViewModel();
        ConfigureGoogleSpendReview(model);
        int invalidations = 0;
        model.ConfigureGoogleRequestInvalidation(() => invalidations++);
        model.ConfigureStage6GoogleGeneration(_ => throw new InvalidOperationException("Must not resolve stale generation."));
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);
        Assert.True(model.CanGenerateWithGoogle);
        if (string.Equals(field, "text", StringComparison.Ordinal)) model.DocumentText = "New content";
        if (string.Equals(field, "voice", StringComparison.Ordinal)) model.SelectedVoiceLabVoice = VoiceSelection();
        if (string.Equals(field, "locale", StringComparison.Ordinal)) model.VoiceLabLocaleFilter = "en-US";
        Assert.False(model.CanGenerateWithGoogle);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.CanApproveGoogleGenerationSpend);
        Assert.True(invalidations > 0);
    }

    [Fact]
    public async Task EditDuringPreparationCannotPublishStaleReview()
    {
        using ShellViewModel model = CreateViewModel();
        var result = new TaskCompletionSource<ShellViewModel.GoogleGenerationSpendReview>();
        model.ConfigureStage6GoogleGenerationPreparation(_ => result.Task);
        Task preparing = model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        model.DocumentText = "Edited during compilation";
        result.SetResult(new("USD", 2, 1, "pricing", "voice", new string('a', 64)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
    }

    [Fact]
    public async Task EditDuringApprovalCannotEnableStaleSubmission()
    {
        using ShellViewModel model = CreateViewModel();
        ConfigureGoogleSpendReview(model);
        var result = new TaskCompletionSource();
        model.ConfigureStage6GoogleGenerationSpendApproval((_, _, _) => result.Task);
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        Task approving = model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);
        model.DocumentText = "Edited while approval was being persisted";
        result.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => approving);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
    }
}

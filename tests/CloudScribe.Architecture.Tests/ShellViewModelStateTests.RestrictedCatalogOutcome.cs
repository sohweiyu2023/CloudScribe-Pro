using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaughtUiQueryFailureUsesTheOuterRefreshMode(bool restricted)
    {
        using ShellViewModel model = CreateViewModel();
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
            Task.FromException<IReadOnlyList<VoiceLabCatalogSelection>>(new OperationCanceledException("private"))),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        bool result = VoiceCatalogRefreshPresentation.RejectIncomplete(model, restricted, model.StatusMessage);
        Assert.False(result);
        Assert.Equal(model.StatusMessage, model.ConnectedVoiceCatalogStatus);
        if (restricted)
        {
            Assert.Contains("Review its recorded state", model.StatusMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Refresh real Google voices to retry", model.StatusMessage, StringComparison.Ordinal);
        }
        else Assert.Contains("Refresh real Google voices to retry", model.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private", model.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedFilterFalseOutcomeCannotOfferRestrictedRetry()
    {
        using ShellViewModel model = CreateViewModel();
        var pending = new TaskCompletionSource<IReadOnlyList<VoiceLabCatalogSelection>>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) => pending.Task),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        Task refresh = model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        model.VoiceLabSearchText = "changed while loading";
        pending.SetResult([]);
        await refresh;
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.False(VoiceCatalogRefreshPresentation.RejectIncomplete(model, true, model.StatusMessage));
        Assert.Contains("Review its recorded state", model.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh voices for the current selection", model.StatusMessage, StringComparison.Ordinal);
    }
}

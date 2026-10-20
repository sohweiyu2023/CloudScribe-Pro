using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task CatalogUiOutcomeDistinguishesSuccessfulEmptyFilterFromFailedRefresh()
    {
        using ShellViewModel model = CreateViewModel();
        int calls = 0;
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
        {
            calls++;
            return calls == 1 ? Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([])
                : Task.FromException<IReadOnlyList<VoiceLabCatalogSelection>>(new InvalidOperationException("secret"));
        }), _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        Assert.True(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Empty(model.VoiceLabCatalogResults);
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.DoesNotContain("secret", model.StatusMessage, StringComparison.Ordinal);
    }
}

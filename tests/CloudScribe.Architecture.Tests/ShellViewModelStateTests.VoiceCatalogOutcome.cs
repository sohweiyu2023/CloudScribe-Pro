using System.Collections.Specialized;
using System.Net;
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

    [Fact]
    public async Task CatalogRefreshClearsSelectionAndApprovalWhileWaitingThenUsesFreshSelection()
    {
        using ShellViewModel model = CreateViewModel();
        VoiceLabCatalogSelection original = VoiceSelection();
        VoiceLabCatalogSelection other = original with { VoiceStableId = "other" };
        VoiceLabCatalogSelection refreshed = original with { LanguageCodes = ["en-US"] };
        TaskCompletionSource<IReadOnlyList<VoiceLabCatalogSelection>> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
            ++calls == 1 ? Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([original, other]) : pending.Task),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        model.SelectedVoiceLabVoice = original;
        ConfigureGoogleSpendReview(model);
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);

        Task refresh = model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);

        Assert.False(model.CanRefreshVoiceLabCatalog);
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        pending.SetResult([refreshed, other]);
        await refresh;

        Assert.True(model.CanRefreshVoiceLabCatalog);
        Assert.True(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Same(refreshed, model.SelectedVoiceLabVoice);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "saved connection")]
    [InlineData(HttpStatusCode.Forbidden, "billing")]
    [InlineData(HttpStatusCode.BadGateway, "internet connection")]
    public async Task FailedRefreshRemovesPriorVoicesAndPreservesSafeRecoveryAction(HttpStatusCode code, string action)
    {
        using ShellViewModel model = CreateViewModel();
        int calls = 0;
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
            ++calls == 2
                ? Task.FromException<IReadOnlyList<VoiceLabCatalogSelection>>(new HttpRequestException("credential-secret private-project", null, code))
                : Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([VoiceSelection()])),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(model.SelectedVoiceLabVoice);

        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.True(model.CanRefreshVoiceLabCatalog);
        Assert.Contains(action, model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential-secret", model.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-project", model.StatusMessage, StringComparison.Ordinal);

        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
        Assert.True(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.NotNull(model.SelectedVoiceLabVoice);
    }

    [Fact]
    public async Task CancelledRefreshDoesNotKeepPreviousVerifiedVoice()
    {
        using ShellViewModel model = CreateViewModel();
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
            Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([VoiceSelection()])),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(model.SelectedVoiceLabVoice);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await model.RefreshVoiceCatalogForUiAsync(cancelled.Token);

        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.True(model.CanRefreshVoiceLabCatalog);
        Assert.Contains("stopped or timed out", model.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringCatalogPublicationRemovesPartialResults()
    {
        using ShellViewModel model = CreateViewModel();
        using CancellationTokenSource cancelled = new();
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) =>
            Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([VoiceSelection()])),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        model.VoiceLabCatalogResults.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add) cancelled.Cancel();
        };

        await model.RefreshVoiceCatalogForUiAsync(cancelled.Token);

        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.True(model.CanRefreshVoiceLabCatalog);
    }
}

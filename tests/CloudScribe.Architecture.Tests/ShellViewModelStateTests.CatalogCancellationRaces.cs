using Avalonia.Controls;
using Avalonia.Interactivity;
using CloudScribe.App.Composition;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(false, "failure")]
    [InlineData(false, "cancel")]
    [InlineData(true, "success")]
    [InlineData(true, "failure")]
    [InlineData(true, "cancel")]
    public async Task CatalogCancellationNewerSameLocaleSetupWinsDuringBootstrap(bool refresh, string outcome)
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        using var model = f.NewModel();
        await f.OpenAsync(model);
        int requests = f.Handler.TotalRequests;
        f.Handler.BlockCatalogAt = f.Handler.CatalogRequests + 1;
        f.Handler.IgnoreBlockedCancellation = true;
        f.Handler.BlockedOutcome = outcome;
        Task<bool> pending = refresh ? f.Loader.RefreshAsync(model, TestContext.Current.CancellationToken)
            : f.Loader.ApplyFiltersAsync(model, TestContext.Current.CancellationToken);
        await f.Handler.Entered.Task;
        model.ApplySavedVoiceSetup(new VoiceSetup("Newer same locale", "en-US", "en-US-Chirp3-HD-Aoede"));
        string status = model.StatusMessage;
        f.Handler.Release.SetResult();
        Assert.False(await pending);
        Assert.Equal("en-US-Chirp3-HD-Aoede", model.PreferredVoiceName);
        Assert.Equal("en-US", model.VoiceLabLocaleFilter);
        Assert.Equal(status, model.StatusMessage);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.False(model.IsConnectedVoiceCatalogLoading);
        Assert.InRange(f.Handler.TotalRequests, requests + 1, requests + 2); // No UI query for the retired selection.
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task CatalogCancellationNewerSelectionAndStatusWinDuringUiQuery(string outcome)
    {
        using var model = CreateViewModel();
        var pending = new TaskCompletionSource<IReadOnlyList<VoiceLabCatalogSelection>>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ConfigureStage7VoiceLabCatalog(new CloudScribe.Application.Generation.VoiceLabCatalogQueryService((_, _) => pending.Task),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        Task query = model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        model.ApplySavedVoiceSetup(new VoiceSetup("Newer", "en-US", "newer"));
        var newer = VoiceSelection() with { VoiceStableId = "newer" };
        model.SelectedVoiceLabVoice = newer;
        model.VoiceLabCatalogResults.Add(newer);
        string status = model.StatusMessage;
        if (string.Equals(outcome, "failure", StringComparison.Ordinal)) pending.SetException(new InvalidOperationException("Old failure"));
        else if (string.Equals(outcome, "cancel", StringComparison.Ordinal)) pending.SetCanceled(TestContext.Current.CancellationToken);
        else pending.SetResult([VoiceSelection()]);
        await query;
        Assert.Same(newer, model.SelectedVoiceLabVoice);
        Assert.Same(newer, Assert.Single(model.VoiceLabCatalogResults));
        Assert.Equal(status, model.StatusMessage);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("window")]
    public Task CatalogComparisonBrokenCancellationCallbackCannotTrapOrRepeatClose(string dismissal) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        await f.OpenAsync(model).ConfigureAwait(true);
        using var ui = new CatalogComparisonUi(f, model);
        f.Handler.BlockCatalogAt = f.Handler.CatalogRequests + 1;
        f.Handler.IgnoreBlockedCancellation = true;
        f.Handler.ThrowCancellationCallback = true;
        Task closed = ui.Open();
        Window dialog = ui.Dialog;
        int closes = 0;
        dialog.Closed += (_, _) => closes++;
        ClickCatalog(dialog, refresh: true);
        await f.Handler.Entered.Task.ConfigureAwait(true);
        Task idle = CatalogIdleAsync(model);
        DismissCatalog(dialog, dismissal);
        await closed.ConfigureAwait(true);
        await f.Handler.CancellationObserved.Task.ConfigureAwait(true);
        DismissCatalog(dialog, "button");
        Assert.Equal(1, closes);
        Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
        Assert.False(f.Handler.Release.Task.IsCompleted);
        f.Handler.Release.SetResult();
        await idle.ConfigureAwait(true);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    [Fact]
    public Task CatalogComparisonCloseRetainsSingleUseDiagnosticClaimUntilFailureUnwinds() => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        using var ui = new CatalogComparisonUi(f, model);
        f.DiagnosticConfiguration = SavedSetupDiagnosticConfiguration();
        f.DiagnosticRunner.PermitOneRefresh = true;
        f.Handler.BlockCatalogAt = f.Handler.CatalogRequests + 1;
        f.Handler.IgnoreBlockedCancellation = true;
        Task closed = ui.Open();
        ClickCatalog(ui.Dialog, refresh: true);
        await f.Handler.Entered.Task.ConfigureAwait(true);
        Task idle = CatalogIdleAsync(model);
        DismissCatalog(ui.Dialog, "button");
        await closed.ConfigureAwait(true);
        Assert.Equal(1, f.DiagnosticRunner.Claims);
        Assert.Empty(f.DiagnosticRunner.Outcomes);
        Task reopened = ui.Open();
        Assert.False(CatalogButton(ui.Dialog, "Refresh connected voices (full verification)").IsEnabled);
        f.Handler.Release.SetResult();
        await idle.ConfigureAwait(true);
        Assert.Equal("Failed", Assert.Single(f.DiagnosticRunner.Outcomes));
        Assert.False(await f.Loader.ApplyFiltersAsync(model, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(1, f.DiagnosticRunner.Claims); // Reopening/apply never reclaims an interrupted diagnostic refresh.
        DismissCatalog(ui.Dialog, "window");
        await reopened.ConfigureAwait(true);
        Assert.Equal(0, f.Handler.PaidRequests);
    });
}

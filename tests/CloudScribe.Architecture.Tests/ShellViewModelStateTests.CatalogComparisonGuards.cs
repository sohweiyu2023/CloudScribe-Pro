using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    private static readonly string[] CatalogDismissalRoutes = ["button", "escape", "window"];

    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("window")]
    public Task CatalogComparisonRestoresOriginalFiltersAndInvokerOnceBeforeLateCompletion(string dismissal) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        model.ApplySavedVoiceSetup(new CloudScribe.App.Composition.VoiceSetup("Original", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam"));
        model.VoiceLabSearchText = "Chirp";
        await f.OpenAsync(model).ConfigureAwait(true);
        using var ui = new CatalogComparisonUi(f, model);
        f.Handler.BlockCatalogAt = f.Handler.CatalogRequests + 1;
        f.Handler.IgnoreBlockedCancellation = true;
        Task closed = ui.Open();
        Window dialog = ui.Dialog;
        ClickCatalog(dialog, refresh: false);
        await f.Handler.Entered.Task.ConfigureAwait(true);
        Task idle = CatalogIdleAsync(model);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(CatalogButton(dialog, "Close voice comparison").Focus());
        int focuses = 0, restorations = 0;
        ui.Invoker.GotFocus += (_, _) => focuses++;
        model.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(ShellViewModel.VoiceLabLocaleFilter)) restorations++; };
        DismissCatalog(dialog, dismissal);
        await closed.ConfigureAwait(true);
        Assert.Equal(1, focuses);
        Assert.Equal(1, restorations);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Equal("Chirp", model.VoiceLabSearchText);
        Assert.Null(model.SelectedVoiceLabVoice); // Never restore an unverified pre-refresh object.
        DismissCatalog(dialog, "button");
        f.Handler.Release.SetResult();
        await idle.ConfigureAwait(true);
        Assert.Equal(1, focuses);
        Assert.Equal(1, restorations);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Equal("Chirp", model.VoiceLabSearchText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CatalogComparisonLoadRetainsExpiryWhileExplicitRefreshReplacesEvidence(bool refresh) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        await f.OpenAsync(model).ConfigureAwait(true);
        var original = model.SelectedVoiceLabVoice;
        var expiry = model.VoiceLanguageCatalogExpiresAtUtc;
        using var ui = new CatalogComparisonUi(f, model);
        Task closed = ui.Open();
        ClickCatalog(ui.Dialog, refresh);
        await CatalogIdleAsync(model).ConfigureAwait(true);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
        if (refresh) Assert.NotEqual(original!.CapabilityEvidenceId, model.SelectedVoiceLabVoice!.CapabilityEvidenceId, StringComparer.Ordinal);
        else
        {
            var current = model.SelectedVoiceLabVoice!;
            Assert.Equal(original!.LanguageCodes!, current.LanguageCodes!);
            Assert.Equal(original with { LanguageCodes = current.LanguageCodes }, current);
            Assert.Equal(expiry, model.VoiceLanguageCatalogExpiresAtUtc);
        }
        DismissCatalog(ui.Dialog, "escape");
        await closed.ConfigureAwait(true);
        Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CatalogComparisonDoesNotDismissPreparationOrGenerationAndDoesNotInferNoCharge(bool generation) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        await f.OpenAsync(model).ConfigureAwait(true);
        using var ui = new CatalogComparisonUi(f, model);
        var prepared = new TaskCompletionSource<ShellViewModel.GoogleGenerationSpendReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = new TaskCompletionSource<GoogleGenerationUiExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ConfigureStage6GoogleGenerationPreparation(_ =>
        {
            if (!generation) entered.TrySetResult();
            return prepared.Task;
        });
        model.ConfigureStage6GoogleGeneration(_ => { entered.TrySetResult(); return executing.Task; });
        Task closed = ui.Open();
        Window dialog = ui.Dialog;
        var close = CatalogButton(dialog, "Close voice comparison");
        CatalogButton(dialog, "Prepare sample and show exact price").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (generation)
        {
            Task ready = CatalogControlEnabledAsync(close);
            prepared.SetResult(new("USD", 2, 1, "synthetic-pricing", "en-US-Neural2-D", new string('a', 64)));
            await ready.ConfigureAwait(true);
            Assert.False(CatalogButton(dialog, "Generate sample").IsEnabled);
            Assert.Single(dialog.GetLogicalDescendants().OfType<CheckBox>()).IsChecked = true;
            CatalogButton(dialog, "Generate sample").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        await entered.Task.ConfigureAwait(true);
        Assert.False(close.IsEnabled);
        foreach (string route in CatalogDismissalRoutes) DismissCatalog(dialog, route);
        Assert.True(dialog.IsVisible);
        Assert.False(closed.IsCompleted);
        Task finished = CatalogControlEnabledAsync(close);
        if (generation) executing.SetException(new InvalidOperationException("Synthetic unresolved pre-dispatch boundary."));
        else prepared.SetResult(new("USD", 2, 1, "synthetic-pricing", "en-US-Neural2-D", new string('a', 64)));
        await finished.ConfigureAwait(true);
        DismissCatalog(dialog, "button");
        await closed.ConfigureAwait(true);
        AssertCatalogSessionCleared(model, f.Handler.PaidRequests);
    });

    private static void AssertCatalogSessionCleared(ShellViewModel model, int paidRequests)
    {
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.Equal(0, paidRequests);
    }

    private static Task CatalogControlEnabledAsync(Control control)
    {
        if (control.IsEnabled) return Task.CompletedTask;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.PropertyChanged += Changed;
        return complete.Task;
        void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != InputElement.IsEnabledProperty || !control.IsEnabled) return;
            control.PropertyChanged -= Changed;
            complete.TrySetResult();
        }
    }
}

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("button", false)]
    [InlineData("escape", false)]
    [InlineData("window", false)]
    [InlineData("escape", true)]
    public Task PreviewCallerCreateMp3RestoresButtonAfterReenabling(string dismissal, bool saveFailure) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        const string original = "Caller preview https://example.org/path stays local.";
        var document = await f.Disk.NewLibrary().CreateAsync("Synthetic caller", original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await model.OpenDocumentCommand.ExecuteAsync(document.Id).ConfigureAwait(true);
        model.ConfigureNarrationCopies(new FailingPreviewCopies());
        using var ui = new CatalogComparisonUi(f, model);
        var shown = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (string.Equals(window.Title, "Create MP3", StringComparison.Ordinal)) shown.TrySetResult(window);
        });
        Task createClosed = (Task)typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("OpenCreateMp3Async", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ui.Binder, [ui.Owner, model, false])!;
        if (await Task.WhenAny(shown.Task, createClosed).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true) == createClosed)
            await createClosed.ConfigureAwait(true);
        Window create = await shown.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await CatalogIdleAsync(model).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Same(create, Assert.Single(ui.Owner.OwnedWindows));
        Assert.Single(create.GetLogicalDescendants().OfType<CheckBox>(), check =>
            string.Equals(Avalonia.Automation.AutomationProperties.GetName(check), "Skip web addresses in narration", StringComparison.Ordinal)).IsChecked = true;
        var button = CatalogButton(create, "Preview and save narration copy");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(button.Focus());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Window preview = Assert.Single(create.OwnedWindows);
        Assert.False(button.IsEnabled);
        if (saveFailure) CatalogButton(preview, "Save and use narration copy").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettlePreviewFocusAsync(preview, text: true).ConfigureAwait(true);
        int focuses = 0;
        button.GotFocus += (_, _) => focuses++;
        DismissNarrationPreview(preview, dismissal);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Empty(create.OwnedWindows);
        Assert.True(button.IsEnabled);
        Assert.Same(button, create.FocusManager!.GetFocusedElement());
        Assert.Equal(1, focuses);
        Assert.Equal(document.Id, model.CurrentDocumentId);
        Assert.Equal(original, model.DocumentText);
        Assert.Equal(0, f.Handler.PaidRequests);
        create.Close();
        await createClosed.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
    });

    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("window")]
    public Task PreviewCallerBatchRestoresButtonAfterExclusiveGuardReenablesIt(string dismissal) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        using var ui = new CatalogComparisonUi(f, model);
        var owner = new Window { Width = 700, Height = 500 };
        using var queue = new FileBatchQueueStore(f.Disk.Paths.RootDirectory);
        object ctx = CreatePreviewBatchContext(owner, model, f, queue, "Actual caller https://example.org/path draft.");
        Type type = ctx.GetType();
        object session = type.GetProperty("Session")!.GetValue(ctx)!;
        session.GetType().GetProperty("Drafts")!.SetValue(session, new FileBatchDraftStore(f.Disk.Paths.RootDirectory));
        var button = (Button)type.GetProperty("PreviewNarration")!.GetValue(ctx)!;
        var list = (ListBox)type.GetProperty("List")!.GetValue(ctx)!;
        owner.Content = new StackPanel { Children = { button, list } };
        typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("WireBatchHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ui.Binder, [ctx]);
        Task parentClosed = owner.ShowDialog(ui.Owner);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(button.Focus());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Window preview = Assert.Single(owner.OwnedWindows);
        Assert.False(button.IsEnabled);
        Assert.True((bool)session.GetType().GetProperty("Busy")!.GetValue(session)!);
        await SettlePreviewFocusAsync(preview, text: true).ConfigureAwait(true);
        int focuses = 0;
        button.GotFocus += (_, _) => focuses++;
        DismissNarrationPreview(preview, dismissal);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Empty(owner.OwnedWindows);
        Assert.True(button.IsEnabled);
        Assert.False((bool)session.GetType().GetProperty("Busy")!.GetValue(session)!);
        Assert.NotNull(list.SelectedItem);
        Assert.Same(button, owner.FocusManager!.GetFocusedElement());
        Assert.Equal(1, focuses);
        Assert.Equal(0, f.Handler.PaidRequests);
        owner.Close();
        await parentClosed.ConfigureAwait(true);
    });
}

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("window")]
    public Task NarrationPreviewDismissesAndReopensWithoutChangingSourceOrReview(string dismissal) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        const string original = "Read https://example.org/path then continue.";
        model.DocumentText = original;
        using var ui = new CatalogComparisonUi(f, model);
        Guid? document = model.CurrentDocumentId, revision = model.CurrentRevisionId;
        int requests = f.Handler.TotalRequests;
        for (int repeat = 0; repeat < 2; repeat++)
        {
            Task<bool> closed = OpenNarrationPreview(ui.Owner, model);
            Window dialog = ui.Dialog;
            Assert.Equal(NarrationUrlOmission.Transform(original).OutputText,
                Assert.Single(dialog.GetLogicalDescendants().OfType<TextBox>()).Text);
            await SettlePreviewFocusAsync(dialog, text: string.Equals(dismissal, "escape", StringComparison.Ordinal)).ConfigureAwait(true);
            int focuses = 0;
            EventHandler<Avalonia.Input.FocusChangedEventArgs> focused = (_, _) => focuses++;
            ui.Invoker.GotFocus += focused;
            DismissNarrationPreview(dialog, dismissal);
            Assert.False(await closed.ConfigureAwait(true));
            ui.Invoker.GotFocus -= focused;
            Assert.Equal(1, focuses);
            Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
            Assert.Equal(original, model.DocumentText);
            Assert.Equal(document, model.CurrentDocumentId);
            Assert.Equal(revision, model.CurrentRevisionId);
            Assert.Null(model.PreparedGoogleGenerationSpendReview);
            Assert.False(model.PreparedGoogleGenerationSpendApproved);
        }
        Assert.Equal(requests, f.Handler.TotalRequests);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    [Fact]
    public Task NarrationPreviewEscapeCannotInterruptSavingAndRestoresInvokerAfterSave() => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        var library = f.Disk.NewLibrary();
        const string original = "Read https://example.org/path then continue.";
        var document = await library.CreateAsync("Synthetic preview", original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await model.OpenDocumentCommand.ExecuteAsync(document.Id).ConfigureAwait(true);
        var copies = new BlockingCopies(CopyStore(f.Disk));
        model.ConfigureNarrationCopies(copies);
        using var ui = new CatalogComparisonUi(f, model);
        Task<bool> closed = OpenNarrationPreview(ui.Owner, model);
        Window dialog = ui.Dialog;
        CatalogButton(dialog, "Save and use narration copy").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await copies.Entered.Task.ConfigureAwait(true);
        try
        {
            Assert.False(CatalogButton(dialog, "Close preview").IsEnabled);
            Assert.True(Assert.Single(dialog.GetLogicalDescendants().OfType<TextBox>()).Focus());
            foreach (string route in CatalogDismissalRoutes) DismissNarrationPreview(dialog, route);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.False(closed.IsCompleted);
            Assert.True(dialog.IsVisible);
            Assert.Equal(document.Id, model.CurrentDocumentId);
        }
        finally { copies.Release.TrySetResult(); }
        Assert.True(await closed.ConfigureAwait(true));
        Assert.Equal(1, copies.Prepares);
        Assert.NotEqual(document.Id, model.CurrentDocumentId);
        Assert.Equal(NarrationUrlOmission.Transform(original).OutputText, model.DocumentText);
        Assert.Equal(original, (await library.OpenAsync(document.Id, TestContext.Current.CancellationToken).ConfigureAwait(true))!.Text);
        Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    private static Task<bool> OpenNarrationPreview(Window owner, ShellViewModel model) =>
        (Task<bool>)typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("PreviewNarrationCopyAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [owner, model, true])!;

    private static async Task SettlePreviewFocusAsync(Window dialog, bool text)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Control target = text ? Assert.Single(dialog.GetLogicalDescendants().OfType<TextBox>()) : CatalogButton(dialog, "Close preview");
        Assert.True(target.Focus());
    }

    private static void DismissNarrationPreview(Window dialog, string dismissal)
    {
        if (string.Equals(dismissal, "button", StringComparison.Ordinal))
            CatalogButton(dialog, "Close preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else if (string.Equals(dismissal, "escape", StringComparison.Ordinal))
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        else dialog.Close();
    }
}

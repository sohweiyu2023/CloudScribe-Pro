using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false, 623, 447, false)]
    [InlineData(true, 623, 447, false)]
    [InlineData(false, 320, 320, false)]
    [InlineData(true, 320, 320, false)]
    [InlineData(false, 623, 447, true)]
    [InlineData(true, 623, 447, true)]
    [InlineData(false, 320, 320, true)]
    [InlineData(true, 320, 320, true)]
    public Task NarrationPreviewConstrainedLayoutKeepsActionsReachable(bool batch, double width, double height, bool longStatus) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        model.DocumentText = string.Concat(Enumerable.Repeat("Synthetic narration https://example.org/path continues. ", 100));
        using var ui = new CatalogComparisonUi(f, model);
        using var queue = new FileBatchQueueStore(f.Disk.Paths.RootDirectory);
        Task closed = batch
            ? (Task)typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("PreviewBatchNarrationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ui.Binder, [CreatePreviewBatchContext(ui.Owner, model, f, queue, model.DocumentText), true])!
            : OpenNarrationPreview(ui.Owner, model);
        Window dialog = ui.Dialog;
        try
        {
            var body = Assert.Single(dialog.GetLogicalDescendants().OfType<StackPanel>());
            if (longStatus) ExpandPreviewLabels(body);
            dialog.Width = width;
            dialog.Height = height;
            await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
            var text = Assert.Single(dialog.GetLogicalDescendants().OfType<TextBox>());
            Assert.True(text.Focus());
            await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
            foreach (Button action in body.Children.OfType<Button>())
            {
                dialog.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, keySymbol: null);
                await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
                Assert.Same(action, dialog.FocusManager!.GetFocusedElement());
                AssertPreviewControlVisible(action, dialog);
            }
            Assert.Equal(320, dialog.MinWidth);
            Assert.Equal(320, dialog.MinHeight);
            var scroll = Assert.IsType<ScrollViewer>(dialog.Content);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.InRange(scroll.Viewport.Width, 1, width);
            Assert.InRange(scroll.Viewport.Height, 1, height);
            await CheckPreviewPointerScrollAndResizeAsync(dialog, scroll).ConfigureAwait(true);
            DismissNarrationPreview(dialog, "escape");
            await closed.ConfigureAwait(true);
            Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
            Assert.Equal(0, f.Handler.PaidRequests);
        }
        finally { dialog.Close(); }
    });

    private static async Task ConstrainCallerPreviewAsync(Window preview)
    {
        preview.Width = 320;
        preview.Height = 320;
        await SettlePreviewLayoutAsync(preview).ConfigureAwait(true);
        Button close = CatalogButton(preview, "Close preview");
        Assert.True(close.Focus());
        await SettlePreviewLayoutAsync(preview).ConfigureAwait(true);
        AssertPreviewControlVisible(close, preview);
    }

    private static void ExpandPreviewLabels(StackPanel body)
    {
        Assert.IsType<TextBlock>(body.Children[0]).Text = string.Concat(Enumerable.Repeat(
            "Long localized status: \u539f\u6587\u4fdd\u6301\u4e0d\u53d8\u3002 Der Originaltext bleibt unver\u00e4ndert. ", 40));
        foreach (Button action in body.Children.OfType<Button>())
            Assert.IsType<TextBlock>(action.Content).Text += " - Erweiterte lokalisierte Beschriftung mit mehreren umgebrochenen Zeilen";
    }

    private static async Task CheckPreviewPointerScrollAndResizeAsync(Window dialog, ScrollViewer scroll)
    {
        // Pointer scrolling must also reveal the final action without relying on focus.
        scroll.Offset = default;
        await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
        dialog.MouseWheel(new Point(10, 10), new Vector(0, -10000));
        await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
        AssertPreviewControlVisible(CatalogButton(dialog, "Close preview"), scroll);
        // A second resize must recompute the viewport, not retain a stale offset/clip.
        dialog.Width = 620;
        dialog.Height = 480;
        await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
        CatalogButton(dialog, "Close preview").BringIntoView();
        await SettlePreviewLayoutAsync(dialog).ConfigureAwait(true);
        AssertPreviewControlVisible(CatalogButton(dialog, "Close preview"), scroll);
    }

    private static async Task SettlePreviewLayoutAsync(Window dialog)
    {
        dialog.UpdateLayout();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        dialog.UpdateLayout();
    }

    private static void AssertPreviewControlVisible(Control control, Control scroll)
    {
        Point position = control.TranslatePoint(default, scroll)!.Value;
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        Assert.InRange(position.X, 0, scroll.Bounds.Width - control.Bounds.Width + 1);
        Assert.InRange(position.Y, 0, scroll.Bounds.Height - control.Bounds.Height + 1);
    }
}

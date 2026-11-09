using Avalonia.Automation.Peers;
using Avalonia.Controls;
using CloudScribe.App.ViewModels;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OutlineAccessibilityPeerNamesUseCurrentParagraphInsteadOfViewModelType(bool drawer) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        await model.NewDocumentCommand.ExecuteAsync(null).ConfigureAwait(true);
        model.DocumentText = "First synthetic paragraph.\nSecond synthetic paragraph.";
        using var ui = new CatalogComparisonUi(f, model);
        ui.Owner.Width = drawer ? 700 : 1600;
        model.UpdateViewport(ui.Owner.Width);
        if (drawer) model.ToggleOutlineDrawerCommand.Execute(null);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        var list = ui.Owner.FindControl<ListBox>(drawer ? "OutlineDrawerList" : "OutlinePanelList")!;
        Assert.True(list.IsEffectivelyVisible);
        ui.Owner.UpdateLayout();
        AssertOutlinePeerName(list, 0, "First synthetic paragraph., Paragraph 1");
        AssertOutlinePeerName(list, 1, "Second synthetic paragraph., Paragraph 2");
        model.DocumentText = "Replacement paragraph.";
        ui.Owner.UpdateLayout();
        AssertOutlinePeerName(list, 0, "Replacement paragraph., Paragraph 1");
        Assert.Single(list.Items);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    private static void AssertOutlinePeerName(ListBox list, int index, string expected)
    {
        var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
        var peer = ControlAutomationPeer.CreatePeerForElement(item)!;
        Assert.Equal(expected, peer.GetName());
        Assert.DoesNotContain(nameof(OutlineEntryViewModel), peer.GetName(), StringComparison.Ordinal);
    }
}

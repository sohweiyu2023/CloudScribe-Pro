using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("window")]
    public Task BatchNarrationPreviewDismissesToItsOwnInvokerWithoutEditingDraft(string dismissal) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        using var ui = new CatalogComparisonUi(f, model);
        var button = new Button { Content = "Synthetic batch preview invoker" };
        var owner = new Window { Content = button, Width = 700, Height = 500 };
        Task parentClosed = owner.ShowDialog(ui.Owner);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(button.Focus());
        const string original = "Synthetic batch https://example.org/path narration.";
        using var queue = new FileBatchQueueStore(f.Disk.Paths.RootDirectory);
        object context = CreatePreviewBatchContext(owner, model, f, queue, original);
        int requests = f.Handler.TotalRequests;
        Task closed = (Task)typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("PreviewBatchNarrationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ui.Binder, [context, true])!;
        Window preview = Assert.Single(owner.OwnedWindows);
        try
        {
            Assert.Equal(CloudScribe.Application.Documents.NarrationUrlOmission.Transform(original).OutputText,
                Assert.Single(preview.GetLogicalDescendants().OfType<TextBox>()).Text);
            await SettlePreviewFocusAsync(preview, text: string.Equals(dismissal, "escape", StringComparison.Ordinal)).ConfigureAwait(true);
            int focuses = 0;
            button.GotFocus += (_, _) => focuses++;
            DismissNarrationPreview(preview, dismissal);
            await closed.ConfigureAwait(true);
            Assert.True(owner.IsVisible);
            Assert.Same(button, owner.FocusManager!.GetFocusedElement());
            Assert.Equal(1, focuses);
            object session = context.GetType().GetProperty("Session")!.GetValue(context)!;
            var texts = (Dictionary<Guid, string>)session.GetType().GetProperty("Texts")!.GetValue(session)!;
            Assert.Equal(original, Assert.Single(texts).Value);
            Assert.Null(session.GetType().GetProperty("Batch")!.GetValue(session));
            Assert.Equal(requests, f.Handler.TotalRequests);
            Assert.Equal(0, f.Handler.PaidRequests);
        }
        finally { owner.Close(); }
        await parentClosed.ConfigureAwait(true);
    });

    private static object CreatePreviewBatchContext(Window owner, ShellViewModel model, CatalogReopenFixture f,
        FileBatchQueueStore queue, string original)
    {
        Type binder = typeof(GoogleTtsUsabilityHotfixBinder);
        Type sessionType = binder.GetNestedType("BatchSession", BindingFlags.NonPublic)!;
        Type contextType = binder.GetNestedType("BatchContext", BindingFlags.NonPublic)!;
        Type rowType = binder.GetNestedType("BatchRowModel", BindingFlags.NonPublic)!;
        object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        sessionType.GetProperty("SkipUrls")!.SetValue(session, true);
        var item = Assert.Single(BatchPlanner.Plan([new BatchSourceFile("synthetic.txt", "synthetic-hash", original.Length, original)],
            _ => "synthetic.mp3", _ => false));
        ((List<BatchItem>)sessionType.GetProperty("Draft")!.GetValue(session)!).Add(item);
        ((Dictionary<Guid, string>)sessionType.GetProperty("Texts")!.GetValue(session)!).Add(item.ItemId, original);
        object context = Activator.CreateInstance(contextType, nonPublic: true)!;
        contextType.GetProperty("Session")!.SetValue(context, session);
        contextType.GetProperty("Dialog")!.SetValue(context, owner);
        contextType.GetProperty("Model")!.SetValue(context, model);
        contextType.GetProperty("Importer")!.SetValue(context, new BatchDocumentImporter(new BoundedLocalDocumentImporter(),
            new(), f.Disk.NewLibrary(), queue));
        var list = (ListBox)contextType.GetProperty("List")!.GetValue(context)!;
        object row = Activator.CreateInstance(rowType, [item, "Synthetic row", null])!;
        list.ItemsSource = new[] { row };
        list.SelectedItem = row;
        return context;
    }
}

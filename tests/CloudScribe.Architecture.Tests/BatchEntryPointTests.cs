using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using CloudScribe.App.Composition;
using CloudScribe.Application.Logging;

namespace CloudScribe.Architecture.Tests;

public sealed class BatchEntryPointTests
{
    [Fact]
    public void ToolbarExposesBatchBesideCreateAndBothQueueSurfacesOfferAnAccessibleDropEntry()
    {
        XDocument window = XDocument.Parse(Source("MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(window.Descendants(), e => string.Equals((string?)e.Attribute(x + "Name"), name, StringComparison.Ordinal));
        XElement create = Named("CreateMp3Button");
        XElement batch = Named("BatchMp3Button");
        Assert.Same(batch, create.ElementsAfterSelf().First());
        Assert.Equal("Batch MP3", (string?)batch.Attribute("Content"));
        Assert.Contains("TXT files and folders", (string?)batch.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
        Assert.Null(batch.Attribute("IsEnabled"));
        Assert.Equal("{Binding IsQueueSelected}", (string?)Named("RouteStateSurface").Attribute("DragDrop.AllowDrop"));
        Assert.Equal("{Binding IsQueueDrawerOpen}", (string?)Named("QueueDrawerSurface").Attribute("DragDrop.AllowDrop"));
        Assert.Equal("{Binding IsQueueSelected}", (string?)Named("QueuePageDropZone").Attribute("IsVisible"));
        foreach (string prefix in new[] { "QueuePage", "QueueDrawer" })
        {
            XElement zone = Named(prefix + "DropZone");
            Assert.Contains(zone.Descendants(), e => string.Equals((string?)e.Attribute("Text"), "Drop TXT files or folders here", StringComparison.Ordinal));
            Assert.Contains(zone.Descendants(), e => ((string?)e.Attribute("Text"))?.Contains("does not generate audio", StringComparison.Ordinal) == true);
            Assert.Contains("choose TXT files or folders", (string?)Named(prefix + "BatchButton").Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
        }
        Assert.Contains("This drawer does not claim that the durable queue is empty.", Source("MainWindow.axaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBatchHasProminentCenteredHeadingAndPickerAlternativeWithoutBlockingDrops()
    {
        MethodInfo factory = typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("CreateBatchEmptyHint", BindingFlags.Static | BindingFlags.NonPublic)!;
        var hint = Assert.IsType<StackPanel>(factory.Invoke(null, null));
        Assert.False(hint.IsHitTestVisible);
        Assert.Equal(VerticalAlignment.Center, hint.VerticalAlignment);
        var heading = Assert.IsType<TextBlock>(hint.Children[0]);
        var alternative = Assert.IsType<TextBlock>(hint.Children[1]);
        Assert.Equal("Drop TXT files or folders here", heading.Text);
        Assert.Equal(heading.Text, AutomationProperties.GetName(heading));
        Assert.True(heading.FontSize > alternative.FontSize);
        Assert.Contains("Add TXT files or Add a folder", alternative.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueDropRestoresDraftBeforeAppendingThroughTheExistingGuardAndDoesNotStartGeneration()
    {
        string entry = Source("Composition", "GoogleTtsUsabilityHotfixBinder.Batch.Entry.cs");
        Assert.Contains("Mount(\"RouteStateSurface\", \"QueuePageDropZone\", \"QueuePageBatchButton\", () => model.IsQueueSelected)", entry, StringComparison.Ordinal);
        Assert.Contains("Mount(\"QueueDrawerSurface\", \"QueueDrawerDropZone\", \"QueueDrawerBatchButton\", () => model.IsQueueDrawerOpen)", entry, StringComparison.Ordinal);
        Assert.Contains("!_batchDialogOpen && e.DataTransfer.Contains(DataFormat.File)", entry, StringComparison.Ordinal);
        Assert.Contains("await OpenBatchAsync(owner, model, items)", entry, StringComparison.Ordinal);
        Assert.Contains("BatchDiagnosticOperation.Drop, BatchDiagnosticPhase.Rejected", entry, StringComparison.Ordinal);
        string batch = Source("Composition", "GoogleTtsUsabilityHotfixBinder.Batch.cs");
        int open = batch.IndexOf("private async Task OpenBatchAsync", StringComparison.Ordinal);
        int layout = batch.IndexOf("private static void BuildBatchLayout", open, StringComparison.Ordinal);
        string opening = batch[open..layout];
        Assert.True(opening.IndexOf("_batchDialogOpen = true", StringComparison.Ordinal) < opening.IndexOf("await ", StringComparison.Ordinal));
        Assert.True(opening.IndexOf("await RestoreDraftAsync(ctx)", StringComparison.Ordinal) < opening.IndexOf("dialog.Opened +=", StringComparison.Ordinal));
        Assert.Contains("GuardBatchAsync(ctx, BatchDiagnosticOperation.Drop,", opening, StringComparison.Ordinal);
        Assert.Contains("() => AddStorageItemsAsync(ctx, dropped, null)", opening, StringComparison.Ordinal);
        Assert.Contains("finally { _batchDialogOpen = false; }", opening, StringComparison.Ordinal);
        Assert.DoesNotContain("StartBatchAsync", entry + opening, StringComparison.Ordinal);
        Assert.DoesNotContain("ApproveBatchAsync", entry + opening, StringComparison.Ordinal);
    }

    [Fact]
    public void FileDropCoversTheWholeListAndStaysSeparateFromPointerReordering()
    {
        string layout = Source("Composition", "GoogleTtsUsabilityHotfixBinder.Batch.cs");
        Assert.Contains("listHost.Children.Add(ctx.List)", layout, StringComparison.Ordinal);
        Assert.Contains("dropStack.Children.Add(listHost)", layout, StringComparison.Ordinal);
        Assert.Contains("ctx.DropZone.Child = dropStack", layout, StringComparison.Ordinal);
        string files = Source("Composition", "GoogleTtsUsabilityHotfixBinder.Batch.Files.cs");
        Assert.Contains("DragDrop.AddDropHandler(ctx.DropZone", files, StringComparison.Ordinal);
        Assert.Contains("e.DataTransfer.TryGetFiles()", files, StringComparison.Ordinal);
        Assert.Contains("GuardBatchAsync(ctx, BatchDiagnosticOperation.Drop, () => AddStorageItemsAsync", files, StringComparison.Ordinal);
        string rows = Source("Composition", "GoogleTtsUsabilityHotfixBinder.Batch.Rows.cs");
        Assert.Contains("InputElement.PointerPressedEvent", rows, StringComparison.Ordinal);
        Assert.Contains("BatchDiagnosticOperation.Reorder", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("AddStorageItemsAsync", rows, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BusyOrRunningBatchRejectsAnotherDropBeforeItsImportCallback(bool busy, bool running)
    {
        var binderType = typeof(GoogleTtsUsabilityHotfixBinder);
        Type sessionType = binderType.GetNestedType("BatchSession", BindingFlags.NonPublic)!;
        Type contextType = binderType.GetNestedType("BatchContext", BindingFlags.NonPublic)!;
        object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        sessionType.GetProperty("Busy")!.SetValue(session, busy);
        if (running) sessionType.GetProperty("Active")!.SetValue(session, new TaskCompletionSource().Task);
        object context = Activator.CreateInstance(contextType, nonPublic: true)!;
        contextType.GetProperty("Session")!.SetValue(context, session);
        object binder = RuntimeHelpers.GetUninitializedObject(binderType);
        int imports = 0;
        Func<Task> import = () => { imports++; return Task.CompletedTask; };
        MethodInfo guard = binderType.GetMethod("GuardBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(guard.Invoke(binder, [context, BatchDiagnosticOperation.Drop, import, true])).ConfigureAwait(true);
        Assert.Equal(0, imports);
        var status = (TextBlock)contextType.GetProperty("Status")!.GetValue(context)!;
        Assert.Contains(running ? "batch is running" : "previous step", status.Text, StringComparison.Ordinal);
    }

    private static string Source(params string[] relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
                return File.ReadAllText(Path.Combine([directory.FullName, "src", "CloudScribe.App", .. relative]));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }
}

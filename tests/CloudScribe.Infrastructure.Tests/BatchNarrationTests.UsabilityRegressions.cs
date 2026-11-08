#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraftUndoSnapshotSurvivesReplacingTheMutableList(bool remove)
    {
        var first = Arrangeable(1, "first.txt");
        var second = Arrangeable(2, "second.txt");
        var draft = new List<BatchItem> { first, second };
        var before = draft.ToArray();
        var changed = remove ? BatchEditor.Remove(before, [first.ItemId], draft: true)
            : BatchEditor.MoveTo(before, [first.ItemId], before.Length);
        draft.Clear();
        draft.AddRange(changed);
        Assert.NotEqual(before.Select(x => x.ItemId), draft.Select(x => x.ItemId));
        var restored = BatchEditor.Restore(draft, before, keepAdded: false);
        Assert.Equal(before.Select(x => x.ItemId), restored.Select(x => x.ItemId));
    }

    [Theory]
    [InlineData("batch.source-changed-add-again")]
    [InlineData("batch.source-missing-add-again")]
    [InlineData("batch.import-interrupted-add-again")]
    public void FailedPreImportCanBeRemovedAndReaddedWithoutLosingAudit(string reason)
    {
        string path = Path.GetFullPath("synthetic-source.txt");
        var failed = Arrangeable(1, "synthetic-source.txt") with { State = BatchItemState.Failed, ReasonCode = reason, SourcePath = path };
        Assert.True(BatchEditor.CanRemove(failed, draft: false));
        var removed = BatchEditor.Remove([failed], [failed.ItemId], draft: false);
        Assert.Equal(failed.ItemId, removed[0].ItemId);
        Assert.Equal(reason, removed[0].ReasonCode);
        Assert.Equal(BatchExclusion.RemovedByUser, removed[0].Exclusion);
        Assert.False(BatchEditor.IsAlreadyListed(removed, path));
        var appended = BatchPlanner.Append(removed, [new BatchSourceFile("synthetic-source.txt", Sha("fresh"), 5, "fresh", SourcePath: path)], t => t + ".mp3", _ => false);
        Assert.Equal(2, appended.Count);
        Assert.NotEqual(failed.ItemId, appended[1].ItemId);
        Assert.Null(appended[1].DocumentId);
        Assert.True(BatchEditor.IsAlreadyListed(appended, path));
        Assert.False(BatchEditor.CanRemove(failed with { JobId = Guid.NewGuid() }, draft: false));
        Assert.False(BatchEditor.CanRemove(failed with { DocumentId = Guid.NewGuid() }, draft: false));
        Assert.False(BatchEditor.CanRemove(failed with { ReasonCode = "provider-failed" }, draft: false));
    }

    [Fact]
    public async Task CachedLocalDraftReadsCurrentBytesAndMissingSourceHasNoText()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        string path = Path.Combine(w.Root, "draft.txt");
        await File.WriteAllTextAsync(path, "old text", Token);
        BatchSourceFile original;
        await using (var stream = File.OpenRead(path)) original = await importer.ReadAsync("draft.txt", stream, 1 << 20, Token) with { SourcePath = path };
        var item = BatchPlanner.Plan([original], t => t + ".mp3", _ => false).Single();
        var cached = new Dictionary<Guid, string> { [item.ItemId] = original.Text! };
        await File.WriteAllTextAsync(path, "current text", Token);
        var fresh = await importer.ReadDraftSourceAsync(item, cached, 1 << 20, Token);
        Assert.Equal("current text", fresh.Text);
        Assert.Equal(Sha("current text"), fresh.Sha256);
        Assert.False(string.Equals(item.SourceSha256, fresh.Sha256, StringComparison.Ordinal));
        File.Delete(path);
        var missing = await importer.ReadDraftSourceAsync(item, cached, 1 << 20, Token);
        Assert.Null(missing.Text);
        Assert.Equal("draft.txt", missing.FileName);
        Assert.Empty(library.Created);
        Assert.Empty(await w.Store.ListAsync(Token));
        var memory = await importer.ReadDraftSourceAsync(item with { SourcePath = null }, cached, 1 << 20, Token);
        Assert.Equal("old text", memory.Text);
        Assert.Equal(item.SourceSha256, memory.Sha256);
    }

    [Fact]
    public async Task SavedPendingImportCannotBypassPinnedHashWithCachedText()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        string path = Path.Combine(w.Root, "pending.txt");
        await File.WriteAllTextAsync(path, "original", Token);
        BatchSourceFile original;
        await using (var stream = File.OpenRead(path)) original = await importer.ReadAsync("pending.txt", stream, 1 << 20, Token) with { SourcePath = path };
        var item = BatchPlanner.Plan([original], t => t + ".mp3", _ => false).Single();
        var cached = new Dictionary<Guid, string> { [item.ItemId] = original.Text! };
        var batch = await w.Store.SaveAsync(new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "pending", 0, BatchQueueState.Ready,
            Settings, Settings.Sha256(), w.Output, "USD", 2, [item], DateTimeOffset.UnixEpoch), Token);
        await File.WriteAllTextAsync(path, "changed", Token);
        var resumed = await importer.ImportAsync(batch, (x, t) => importer.ReadPendingAsync(x, cached, 1 << 20, t), null, Token);
        Assert.Empty(library.Created);
        Assert.Equal(item.SourceSha256, resumed.Items[0].SourceSha256);
        Assert.Equal("batch.source-changed-add-again", resumed.Items[0].ReasonCode);
        File.Delete(path);
        Assert.Equal((null, "batch.source-missing-add-again"), await importer.ReadPendingAsync(item, cached, 1 << 20, Token));
        Assert.Equal(("original", (string?)null), await importer.ReadPendingAsync(item with { SourcePath = null }, cached, 1 << 20, Token));
        Assert.Equal((null, "batch.import-interrupted-add-again"), await importer.ReadPendingAsync(item with { SourcePath = null }, new Dictionary<Guid, string>(), 1 << 20, Token));
        await File.WriteAllTextAsync(path, "original", Token);
        var finished = await importer.ImportAsync(resumed, (x, t) => importer.ReadPendingAsync(x, cached, 1 << 20, t), null, Token);
        Assert.Single(library.Created);
        Assert.Equal(BatchItemState.Ready, finished.Items[0].State);
        Assert.NotNull(finished.Items[0].DocumentId);
    }
}

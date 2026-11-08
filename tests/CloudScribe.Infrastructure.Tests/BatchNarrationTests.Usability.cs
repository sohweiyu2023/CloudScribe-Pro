#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

/// <summary>Round 3 (1.0.4): arranging files, live duplicate notes, drafts that survive closing, re-reading files with
/// their hash checked, resumable adds, and the earlier-charge marker. Synthetic files only; no provider is contacted.</summary>
public sealed partial class BatchNarrationTests
{
    private static BatchItem Arrangeable(int order, string name, string? sha = null) =>
        new(Guid.NewGuid(), order, name, sha ?? Sha(name), 10, 10, System.IO.Path.GetFileNameWithoutExtension(name) + ".mp3", BatchItemState.Ready);

    private static string[] Names(IEnumerable<BatchItem> items) => items.Select(x => x.SourceFileName).ToArray();

    [Fact]
    public void MoveToKeepsRelativeOrderAndNeverMovesStartedFiles()
    {
        var a = Arrangeable(1, "a"); var b = Arrangeable(2, "b"); var c = Arrangeable(3, "c"); var d = Arrangeable(4, "d");
        var done = Arrangeable(5, "done") with { State = BatchItemState.Completed, JobId = Guid.NewGuid() };
        IReadOnlyList<BatchItem> items = [a, b, c, d, done];
        Assert.Equal(["b", "d", "a", "c", "done"], Names(BatchEditor.MoveTo(items, [a.ItemId, c.ItemId], 4)));
        Assert.Equal(["b", "d", "a", "c", "done"], Names(BatchEditor.MoveTo(items, [c.ItemId, a.ItemId], 4))); // selection order does not matter
        Assert.Equal(["c", "d", "a", "b", "done"], Names(BatchEditor.MoveTo(items, [c.ItemId, d.ItemId], 0)));
        Assert.Equal(["b", "c", "d", "done", "a"], Names(BatchEditor.MoveTo(items, [a.ItemId], items.Count)));
        Assert.Same(items, BatchEditor.MoveTo(items, [b.ItemId, c.ItemId], 2)); // dropped inside itself: unchanged
        Assert.Same(items, BatchEditor.MoveTo(items, [done.ItemId], 0)); // a finished file never moves
        Assert.Equal(["a", "b", "c", "done", "d"], Names(BatchEditor.MoveTo(items, [d.ItemId, done.ItemId], 99))); // clamped; the finished file stays
    }

    [Fact]
    public void MoveByMovesBlocksTogetherAndStopsAtEdgesAndStartedFiles()
    {
        var a = Arrangeable(1, "a"); var b = Arrangeable(2, "b"); var c = Arrangeable(3, "c"); var d = Arrangeable(4, "d"); var e = Arrangeable(5, "e");
        IReadOnlyList<BatchItem> items = [a, b, c, d, e];
        Assert.Equal(["a", "c", "d", "b", "e"], Names(BatchEditor.MoveBy(items, [c.ItemId, d.ItemId], -1)));
        Assert.Equal(["a", "b", "e", "c", "d"], Names(BatchEditor.MoveBy(items, [c.ItemId, d.ItemId], 1)));
        Assert.Equal(["b", "a", "d", "c", "e"], Names(BatchEditor.MoveBy(items, [b.ItemId, d.ItemId], -1))); // two separate blocks
        Assert.Same(items, BatchEditor.MoveBy(items, [a.ItemId, b.ItemId], -1)); // already at the top
        Assert.Same(items, BatchEditor.MoveBy(items, [e.ItemId], 1));
        var running = c with { State = BatchItemState.Running, JobId = Guid.NewGuid() };
        IReadOnlyList<BatchItem> withRunning = [a, b, running, d, e];
        // A running file is a fixed point; a waiting neighbour can still pass it, but it never moves itself.
        Assert.Equal(["a", "b", "d", "c", "e"], Names(BatchEditor.MoveBy(withRunning, [d.ItemId], -1)));
        Assert.Same(withRunning, BatchEditor.MoveBy(withRunning, [running.ItemId], -1));
    }

    [Fact]
    public void RemoveDeletesFromDraftsButKeepsHistoryInSavedBatchesAndUndoRestores()
    {
        var a = Arrangeable(1, "a"); var b = Arrangeable(2, "b");
        var bad = Arrangeable(3, "bad") with { State = BatchItemState.Excluded, Exclusion = BatchExclusion.NotUnicodeText };
        var started = Arrangeable(4, "started") with { JobId = Guid.NewGuid() };
        IReadOnlyList<BatchItem> items = [a, b, bad, started];
        var draft = BatchEditor.Remove(items, [a.ItemId, bad.ItemId, started.ItemId], draft: true);
        Assert.Equal(["b", "started"], Names(draft)); // unusable rows can be cleared from a draft; started files never
        var saved = BatchEditor.Remove(items, [a.ItemId, started.ItemId], draft: false);
        Assert.Equal(BatchExclusion.RemovedByUser, saved[0].Exclusion);
        Assert.Equal(BatchItemState.Ready, saved[3].State);

        Assert.Equal(["a", "b", "bad", "started"], Names(BatchEditor.Restore(draft, items, keepAdded: false)));
        var restored = BatchEditor.Restore(saved, items);
        Assert.Equal(BatchItemState.Ready, restored[0].State);
        Assert.Equal(BatchExclusion.None, restored[0].Exclusion);

        // Undo never rolls back progress: a file that started after the snapshot keeps its state.
        var progressed = saved.Select(x => x.ItemId == b.ItemId ? x with { State = BatchItemState.Completed, JobId = Guid.NewGuid() } : x).ToArray();
        Assert.Equal(BatchItemState.Completed, BatchEditor.Restore(progressed, items).Single(x => x.ItemId == b.ItemId).State);

        // Undoing an add in a draft takes the added files out; in a saved batch they stay (they are in the library).
        var added = Arrangeable(5, "added");
        Assert.Equal(["a", "b", "bad", "started"], Names(BatchEditor.Restore([.. items, added], items, keepAdded: false)));
        Assert.Equal(["a", "b", "bad", "started", "added"], Names(BatchEditor.Restore([.. items, added], items, keepAdded: true)));
    }

    [Fact]
    public void SameTextNotesFollowTheCurrentOrderAfterMovesAndRemovals()
    {
        var first = Arrangeable(1, "First.txt", "same");
        var other = Arrangeable(2, "Other.txt");
        var second = Arrangeable(3, "Second.txt", "same");
        var third = Arrangeable(4, "Third.txt", "same");
        var named = BatchPlanner.AssignOutputNames([first, other, second, third], t => t + ".mp3", _ => false);
        Assert.Equal([null, null, 1, 1], named.Select(x => x.SameTextAs).ToArray());

        var moved = BatchPlanner.AssignOutputNames(BatchEditor.MoveTo(named, [third.ItemId], 0), t => t + ".mp3", _ => false);
        Assert.Equal(["Third.txt", "First.txt", "Other.txt", "Second.txt"], Names(moved));
        Assert.Equal([null, 1, null, 1], moved.Select(x => x.SameTextAs).ToArray()); // the note points at the new first copy

        var removed = BatchPlanner.AssignOutputNames(BatchEditor.Remove(moved, [third.ItemId], draft: true), t => t + ".mp3", _ => false);
        Assert.Equal([null, null, 1], removed.Select(x => x.SameTextAs).ToArray()); // never a stale row number
        Assert.Equal(3, removed.Count(x => x.IsIncluded)); // identical text is still one MP3 per file
    }

    [Fact]
    public void TrackNumbersUseIncludedFilesAndNeverDoubleNumberOrOverwrite()
    {
        var files = TrackNames.Select((n, i) => Arrangeable(i + 1, n, n + i)).ToList();
        for (int i = 0; i < 99; i++) files.Add(Arrangeable(files.Count + 1, $"x{i}.txt") with { State = BatchItemState.Excluded, Exclusion = BatchExclusion.Empty });
        var named = BatchPlanner.AssignOutputNames(files, t => t + ".mp3", n => string.Equals(n, "03 Intro.mp3", StringComparison.Ordinal), numberTracks: true);
        Assert.Equal("01 Preface.mp3", named[0].OutputFileName); // already numbered: not "01 01 Preface"
        Assert.Equal("02 Intro.mp3", named[1].OutputFileName); // two digits: only 3 files are narrated, not 102
        Assert.Equal("03 Intro (2).mp3", named[2].OutputFileName); // exists in the folder: never overwritten
        Assert.Equal(2, BatchPlanner.TrackWidth(named));
    }

    [Fact]
    public void AppendKeepsExistingOrderAndCanKeepTheCallersFolderOrder()
    {
        var existing = BatchPlanner.Plan([Source("b.txt"), Source("a.txt")], t => t + ".mp3", _ => false);
        Assert.Equal(["a.txt", "b.txt"], Names(existing));
        var arranged = BatchEditor.MoveTo(existing, [existing[1].ItemId], 0);
        var appended = BatchPlanner.Append(arranged, [Source("z/10.txt", "10.txt"), Source("a/2.txt", "2.txt")], t => t + ".mp3", _ => false, keepGivenOrder: true);
        Assert.Equal(["b.txt", "a.txt", "10.txt", "2.txt"], Names(appended)); // the caller sorted by folder; that order is kept
        Assert.Equal("z/10.txt", appended[2].SourcePath);
        var natural = BatchPlanner.Append(arranged, [Source("10.txt"), Source("2.txt")], t => t + ".mp3", _ => false);
        Assert.Equal(["b.txt", "a.txt", "2.txt", "10.txt"], Names(natural));
        Assert.True(BatchEditor.IsAlreadyListed(appended, "z/10.txt"));
        Assert.False(BatchEditor.IsAlreadyListed(appended, "z/11.txt"));

        static BatchSourceFile Source(string path, string? name = null) => new(name ?? path, Sha(path), 10, "text " + path, SourcePath: path);
    }

    [Fact]
    public async Task DraftStoreKeepsNamesAndHashesButNeverTextAndSurvivesADamagedFile()
    {
        using var w = new Workspace();
        var store = new FileBatchDraftStore(System.IO.Path.Combine(w.Root, "profile"));
        Assert.Null(await store.LoadAsync(Token));
        var items = BatchPlanner.Plan([new BatchSourceFile("無我.txt", Sha("x"), 9, "PRIVATE_DRAFT_TEXT", SourcePath: "C:\\in\\無我.txt")], t => t + ".mp3", _ => false);
        var draft = BatchDraft.Empty(DateTimeOffset.UnixEpoch) with { Name = "Draft", OutputFolder = w.Output, NumberTracks = true, Items = items };
        await store.SaveAsync(draft, Token);
        await store.SaveAsync(draft with { Name = "Draft 2" }, Token);
        string json = await File.ReadAllTextAsync(store.FilePath, Token);
        Assert.DoesNotContain("PRIVATE_DRAFT_TEXT", json, StringComparison.Ordinal);
        var loaded = await store.LoadAsync(Token);
        Assert.Equal("Draft 2", loaded!.Name);
        Assert.Equal("C:\\in\\無我.txt", loaded.Items.Single().SourcePath);
        Assert.True(loaded.NumberTracks);

        await File.WriteAllTextAsync(store.FilePath, "{ truncated", Token); // crash mid-write on an old version
        Assert.Equal("Draft", (await store.LoadAsync(Token))!.Name); // the previous copy
        await File.WriteAllTextAsync(store.FilePath + ".bak", "{ also bad", Token);
        Assert.Null(await store.LoadAsync(Token));
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(store.FilePath)!, "draft.json.damaged-*")); // kept, never deleted
        await store.DeleteAsync(Token);
        Assert.False(File.Exists(store.FilePath));
        Assert.Empty(await w.Store.ListAsync(Token)); // the draft is never listed as a saved batch
    }

    [Fact]
    public async Task ReadAgainReturnsTextOnlyForTheSameBytes()
    {
        using var w = new Workspace();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), new MemoryLibrary(), w.Store);
        string path = System.IO.Path.Combine(w.Root, "第一章.txt");
        await File.WriteAllTextAsync(path, "第一章 synthetic", Token);
        BatchSourceFile read;
        await using (var stream = File.OpenRead(path)) read = await importer.ReadAsync("第一章.txt", stream, 1 << 20, Token) with { SourcePath = path };
        var item = BatchPlanner.Plan([read], t => t + ".mp3", _ => false).Single();
        Assert.Equal(("第一章 synthetic", (string?)null), await importer.ReadAgainAsync(item, 1 << 20, Token));
        await File.WriteAllTextAsync(path, "changed", Token);
        Assert.Equal((null, "batch.source-changed-add-again"), await importer.ReadAgainAsync(item, 1 << 20, Token));
        File.Delete(path);
        Assert.Equal((null, "batch.source-missing-add-again"), await importer.ReadAgainAsync(item, 1 << 20, Token));
        Assert.Equal((null, "batch.import-interrupted-add-again"), await importer.ReadAgainAsync(item with { SourcePath = null }, 1 << 20, Token));
    }

    [Fact]
    public async Task InterruptedAddResumesByReadingFilesAgainAndNeverAddsTwice()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        var sources = new List<BatchSourceFile>();
        foreach (string name in new[] { "1.txt", "2.txt", "3.txt" })
        {
            string path = System.IO.Path.Combine(w.Root, name);
            await File.WriteAllTextAsync(path, "Synthetic " + name, Token);
            await using var stream = File.OpenRead(path);
            sources.Add(await importer.ReadAsync(name, stream, 1 << 20, Token) with { SourcePath = path });
        }
        var items = BatchPlanner.Plan(sources, t => t + ".mp3", _ => false);
        var batch = await w.Store.SaveAsync(new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "b", 0, BatchQueueState.Ready,
            Settings, Settings.Sha256(), w.Output, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);

        // The user cancels after the first file.
        using var cancel = new CancellationTokenSource();
        var reports = new List<(int, int)>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(batch, async (item, token) =>
        {
            var result = await importer.ReadAgainAsync(item, 1 << 20, token);
            await cancel.CancelAsync();
            return result;
        }, new SyncProgress<(int, int)>(reports.Add), cancel.Token));
        Assert.Single(library.Created);
        var saved = await w.Store.LoadAsync(batch.BatchId, Token);
        Assert.Equal(2, saved!.Items.Count(BatchDocumentImporter.NeedsImport));

        // One source changed meanwhile: it is refused, not narrated with different text.
        await File.WriteAllTextAsync(System.IO.Path.Combine(w.Root, "3.txt"), "edited later", Token);
        var resumed = await importer.ImportAsync(saved, (item, token) => importer.ReadAgainAsync(item, 1 << 20, token), null, Token);
        Assert.Equal(2, library.Created.Count); // file 1 was not added again
        Assert.Equal("batch.source-changed-add-again", resumed.Items[2].ReasonCode);
        Assert.True(BatchDocumentImporter.NeedsImport(resumed.Items[2]));
        Assert.Equal((0, 3), reports[0]);

        // After the user restores the file, opening the batch again finishes the add.
        await File.WriteAllTextAsync(System.IO.Path.Combine(w.Root, "3.txt"), "Synthetic 3.txt", Token);
        var finished = await importer.ImportAsync(resumed, (item, token) => importer.ReadAgainAsync(item, 1 << 20, token), null, Token);
        Assert.Equal(3, library.Created.Count);
        Assert.All(finished.Items, x => Assert.Equal(BatchItemState.Ready, x.State));
        Assert.DoesNotContain(finished.Items, BatchDocumentImporter.NeedsImport);
    }

    [Fact]
    public async Task UncertainOutcomeLeavesAPermanentPossibleChargeMarker()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var narrator = new FakeNarrator();
        narrator.Behaviour[1] = (_, _) => Task.FromResult(new BatchNarrationOutcome(BatchOutcomeKind.Uncertain, "article.provider-outcome-uncertain-no-retry"));
        var runner = new BatchQueueRunner(w.Store, narrator, TimeProvider.System);
        var after = await runner.RunAsync(batch.BatchId, Settings.Sha256(), null, null, Token);
        Assert.Equal(1, after.Items[0].UncertainOutcomes);
        var reconciled = await w.Store.SaveAsync(BatchQueueRunner.CheckAgain(after, after.Items[0].ItemId), Token);
        narrator.Behaviour.Clear();
        var done = await runner.RunAsync(batch.BatchId, Settings.Sha256(), null, null, Token);
        Assert.Equal(BatchItemState.Completed, done.Items[0].State);
        Assert.Equal(1, done.Items[0].UncertainOutcomes); // replacement audio never hides the earlier possible charge
        Assert.Equal(reconciled.BatchId, done.BatchId);
    }

    private static readonly string[] TrackNames = ["01 Preface.txt", "Intro.txt", "Intro.txt"];

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

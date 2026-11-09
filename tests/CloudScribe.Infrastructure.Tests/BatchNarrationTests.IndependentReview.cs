#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

/// <summary>Independent review (9 Nov 2026): long Windows file names, character-safe titles, one refused file not
/// stopping a batch import, and folder scans that cannot loop through links or fail on one unreadable subfolder.</summary>
public sealed partial class BatchNarrationTests
{
    private const string ExtensionB = "\U00020000"; // 𠀀, a CJK Extension B character (a surrogate pair in UTF-16).

    [Fact]
    public void DocumentTitlesAreFittedOnCharacterBoundaries()
    {
        string title = new string('甲', 199) + ExtensionB + "tail";
        string fitted = DocumentTitleText.Fit(title, 200);
        Assert.Equal(new string('甲', 199), fitted); // the pair at 199..200 is not split
        Assert.False(char.IsHighSurrogate(fitted[^1]));
        Assert.Equal("short", DocumentTitleText.Fit("  short  ", 200));
        Assert.Equal("é", DocumentTitleText.Fit("éx", 2)); // a combining sequence stays whole
        Assert.Equal("\u2026", DocumentTitleText.Fit(ExtensionB + "a", 1)); // a bounded placeholder when no whole element fits

        string longName = new string('a', 245) + ".txt"; // valid on Windows (up to 255 characters)
        Assert.Equal(240, BatchPlanner.DocumentTitleOf(longName).Length);
        Assert.Equal(245, BatchPlanner.TitleOf(longName).Length); // output MP3 names keep the full name
    }

    [Theory]
    [InlineData("e\u0301x", 1, "\u2026")]
    [InlineData("e\u0301x", 2, "e\u0301")]
    [InlineData("\U0001F600x", 1, "\u2026")]
    [InlineData("\U0001F600x", 2, "\U0001F600")]
    [InlineData("  x  ", 1, "x")]
    [InlineData("   ", 1, "")]
    [InlineData("a e\u0301", 2, "a")]
    public void TitleLimitsRespectWholeGraphemesAndWhitespace(string title, int limit, string expected)
    {
        Assert.Equal(expected, DocumentTitleText.Fit(title, limit));
        Assert.True(expected.Length <= limit);
    }

    [Fact]
    public async Task OversizedGraphemeFilenameUsesBoundedTitleWithoutChangingTextOrOutputName()
    {
        string stem = "e" + new string('\u0301', 240);
        string name = stem + ".txt";
        Assert.Equal("\u2026", DocumentTitleText.Fit(stem, 240));
        Assert.Equal("\u2026", BatchPlanner.DocumentTitleOf(name));
        Assert.Equal(stem, BatchPlanner.TitleOf(name));
        using var source = new MemoryStream("source text"u8.ToArray());
        var result = await new BoundedLocalDocumentImporter().ImportAsync(new(LocalDocumentImportKind.PlainText, name, source, 11), Token);
        Assert.Equal("\u2026", result.SuggestedTitle);
        using var w = new Workspace();
        var library = new StrictTitleLibrary();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        var items = BatchPlanner.Plan([new BatchSourceFile(name, Sha("source text"), 11, "source text")], t => t + ".mp3", _ => false);
        var batch = await w.Store.SaveAsync(new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "b", 0, BatchQueueState.Ready,
            Settings, Settings.Sha256(), w.Output, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);
        var imported = await importer.ImportAsync(batch, items.ToDictionary(x => x.ItemId, _ => "source text"), Token);
        Assert.Equal("\u2026", Assert.Single(library.Created).Title);
        Assert.Equal("source text", Assert.Single(library.Created).Text);
        Assert.Equal(stem + ".mp3", Assert.Single(imported.Items).OutputFileName);
        Assert.Equal(BatchItemState.Ready, Assert.Single(imported.Items).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderScanRejectsALinkAsItsRootAndAnAncestorLoop(bool junction)
    {
        using var w = new Workspace();
        string target = System.IO.Path.Combine(w.Root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(System.IO.Path.Combine(target, "kept.txt"), "x");
        string link = System.IO.Path.Combine(w.Root, "link");
        if (!(junction ? TryJunction(link, target) : TryLink(link, target)))
        { Assert.Skip("Host did not permit this temporary directory link; no security setting was changed."); return; }
        try
        {
            foreach (bool recursive in new[] { false, true })
            {
                var scan = BatchFolderScanner.Scan(link, recursive, Token);
                Assert.Empty(scan.Files);
                Assert.Equal(1, scan.SkippedLinks);
                Assert.Equal(0, scan.UnreadableFolders);
            }
            string loop = System.IO.Path.Combine(target, "loop");
            Assert.True(junction ? TryJunction(loop, target) : TryLink(loop, target));
            try
            {
                var scan = BatchFolderScanner.Scan(target, recursive: true, Token);
                Assert.Single(scan.Files);
                Assert.Equal(1, scan.SkippedLinks);
            }
            finally { Directory.Delete(loop); }
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task SingleDocumentImportTitlesNeverSplitASurrogatePair()
    {
        var importer = new BoundedLocalDocumentImporter();
        string name = new string('乙', 199) + ExtensionB + ".txt";
        using var source = new MemoryStream("text"u8.ToArray());
        var result = await importer.ImportAsync(new(LocalDocumentImportKind.PlainText, name, source, 4), Token);
        Assert.Equal(new string('乙', 199), result.SuggestedTitle);
        Assert.DoesNotContain(result.SuggestedTitle, char.IsSurrogate);
    }

    [Fact]
    public async Task LongFileNamesImportAndOneRefusedFileDoesNotStopTheRest()
    {
        using var w = new Workspace();
        var library = new StrictTitleLibrary { Refuse = "poison" };
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        string longName = new string('長', 245) + ".txt";
        var items = BatchPlanner.Plan(
            [new BatchSourceFile("1 first.txt", Sha("1"), 5, "one"), new BatchSourceFile("2 poison.txt", Sha("2"), 5, "two"), new BatchSourceFile("3 " + longName, Sha("3"), 5, "three")],
            t => t + ".mp3", _ => false);
        var batch = await w.Store.SaveAsync(new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "b", 0, BatchQueueState.Ready,
            Settings, Settings.Sha256(), w.Output, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);
        var texts = items.ToDictionary(x => x.ItemId, x => x.SourceFileName);

        var imported = await importer.ImportAsync(batch, texts, Token);

        Assert.Equal(2, library.Created.Count); // files before and after the refused one were added
        Assert.Equal(240, library.Created[1].Title.Length); // the long name fits the library's limit
        Assert.Equal(BatchItemState.Ready, imported.Items[0].State);
        Assert.Equal("batch.source-import-refused", imported.Items[1].ReasonCode);
        Assert.True(BatchDocumentImporter.NeedsImport(imported.Items[1])); // blocks Start until removed or fixed
        Assert.True(BatchEditor.CanRemove(imported.Items[1], draft: false));
        Assert.Equal(BatchItemState.Ready, imported.Items[2].State);
        Assert.EndsWith(".mp3", imported.Items[2].OutputFileName, StringComparison.Ordinal);
        Assert.StartsWith("3 " + new string('長', 245), imported.Items[2].OutputFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderScanFindsTxtInNaturalOrderAndNeverFollowsLinkLoops()
    {
        using var w = new Workspace();
        string root = System.IO.Path.Combine(w.Root, "scan");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "Part 10"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "Part 2", "deep", "deeper"));
        File.WriteAllText(System.IO.Path.Combine(root, "10.txt"), "x");
        File.WriteAllText(System.IO.Path.Combine(root, "2.TXT"), "x");
        File.WriteAllText(System.IO.Path.Combine(root, "notes.md"), "x");
        File.WriteAllText(System.IO.Path.Combine(root, "Part 10", "a.txt"), "x");
        File.WriteAllText(System.IO.Path.Combine(root, "Part 2", "deep", "deeper", "b.txt"), "x");
        bool linked = TryLink(System.IO.Path.Combine(root, "Part 2", "deep", "loop"), root);

        var flat = BatchFolderScanner.Scan(root, recursive: false, Token);
        Assert.Equal(["2.TXT", "10.txt"], flat.Files.Select(System.IO.Path.GetFileName).ToArray());
        Assert.Equal(0, flat.SkippedLinks);

        var deep = BatchFolderScanner.Scan(root, recursive: true, Token);
        Assert.Equal(["2.TXT", "10.txt", "b.txt", "a.txt"], deep.Files.Select(System.IO.Path.GetFileName).ToArray());
        Assert.Equal(linked ? 1 : 0, deep.SkippedLinks); // the loop back to the root is reported, not followed

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => BatchFolderScanner.Scan(root, recursive: true, cancelled.Token));
    }

    [Fact]
    public void FolderScanCountsAMissingFolderInsteadOfThrowing()
    {
        using var w = new Workspace();
        var scan = BatchFolderScanner.Scan(System.IO.Path.Combine(w.Root, "gone"), recursive: true, Token);
        Assert.Empty(scan.Files);
        Assert.Equal(1, scan.UnreadableFolders);
    }

    [Fact]
    public void DuplicatePathCheckIsLinearAndToleratesMalformedSavedPaths()
    {
        var items = Enumerable.Range(0, 5000).Select(i => Arrangeable(i + 1, $"{i}.txt") with { SourcePath = $"/books/{i}.txt" }).ToList();
        items.Add(Arrangeable(5001, "bad.txt") with { SourcePath = "bad\0path" });
        items.Add(Arrangeable(5002, "gone.txt") with { SourcePath = "/books/gone.txt", State = BatchItemState.Excluded, Exclusion = BatchExclusion.RemovedByUser });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var listed = BatchEditor.ListedPaths(items);
        int found = Enumerable.Range(0, 5000).Count(i => BatchEditor.Normalize($"/books/{i}.txt") is { } full && listed.Contains(full));
        Assert.Equal(5000, found);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10)); // was 25 million path normalisations before
        Assert.False(BatchEditor.IsAlreadyListed(items, "/books/gone.txt")); // removed files may be added again
        Assert.Null(BatchEditor.Normalize("bad\0path"));
        Assert.False(BatchEditor.IsAlreadyListed(items, null));
    }

    private static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    private static bool TryJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:CLOUDSCRIBE_TEST_LINK -Target $env:CLOUDSCRIBE_TEST_TARGET | Out-Null");
        start.Environment["CLOUDSCRIBE_TEST_LINK"] = link;
        start.Environment["CLOUDSCRIBE_TEST_TARGET"] = target;
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>A library that enforces the real 240-character title limit and can refuse one named title.</summary>
    private sealed class StrictTitleLibrary : IDocumentLibrary
    {
        private readonly MemoryLibrary _inner = new();

        public string? Refuse { get; init; }

        public List<DocumentSnapshot> Created => _inner.Created;

        public Task<DocumentSnapshot> CreateAsync(string title, string text, CancellationToken cancellationToken = default)
        {
            if (title.Trim().Length is < 1 or > DocumentTitleText.LibraryMaximum || Refuse is { } refused && title.Contains(refused, StringComparison.Ordinal))
                throw new ArgumentException("Document title must contain 1 to 240 characters.", nameof(title));
            return _inner.CreateAsync(title, text, cancellationToken);
        }

        public Task<DocumentSnapshot> SaveAsync(DocumentSaveRequest request, CancellationToken cancellationToken = default) => _inner.SaveAsync(request, cancellationToken);

        public Task<DocumentSnapshot?> OpenAsync(Guid documentId, CancellationToken cancellationToken = default) => _inner.OpenAsync(documentId, cancellationToken);

        public Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentStatus status = DocumentStatus.Active, int limit = 100, CancellationToken cancellationToken = default) => _inner.ListAsync(status, limit, cancellationToken);

        public Task<IReadOnlyList<DocumentSummary>> SearchAsync(string query, DocumentStatus status = DocumentStatus.Active, int limit = 50, CancellationToken cancellationToken = default) => _inner.SearchAsync(query, status, limit, cancellationToken);

        public Task<DocumentSummary> ChangeStatusAsync(Guid documentId, DocumentStatus status, long expectedConcurrencyVersion, CancellationToken cancellationToken = default) => _inner.ChangeStatusAsync(documentId, status, expectedConcurrencyVersion, cancellationToken);
    }
}

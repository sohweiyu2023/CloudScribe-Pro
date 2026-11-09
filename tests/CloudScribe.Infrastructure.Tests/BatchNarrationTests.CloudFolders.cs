#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void EveryDocumentedCloudTagAllowsRootAndNestedFolders(int variant)
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "Part 2")).FullName;
        File.WriteAllText(Path.Combine(root, "10.txt"), "ten");
        File.WriteAllText(Path.Combine(root, "2.TXT"), "two");
        File.WriteAllText(Path.Combine(nested, "1.txt"), "nested");
        File.WriteAllText(Path.Combine(root, "ignored.md"), "ignored");
        var fs = new FolderFileSystem();
        fs.Tags[root] = fs.Tags[nested] = 0x9000001Au | ((uint)variant << 12);
        // Cloud TXT files are listed normally, without asking for their content or reparse data.
        fs.Attributes[Path.Combine(root, "2.TXT")] = FileAttributes.Archive | FileAttributes.ReparsePoint;
        var flat = BatchFolderScanner.Scan(root, false, fs, Token);
        Assert.Equal(["2.TXT", "10.txt"], flat.Files.Select(Path.GetFileName).ToArray());
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Equal(["2.TXT", "10.txt", "1.txt"], scan.Files.Select(Path.GetFileName).ToArray());
        Assert.Equal(0, scan.SkippedLinks);
        Assert.Equal(0, scan.UnreadableFolders);
        Assert.Equal(2, fs.TagReads[nested]); // discovery and the queued-folder recheck
        Assert.All(fs.TagReads.Keys, path => Assert.True(Directory.Exists(path)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xA0000003u)] // junction/mount point
    [InlineData(0xA000000Cu)] // symbolic link
    [InlineData(0x9000001Cu)] // Projected File System is not a Cloud Files tag
    [InlineData(0x80000015u)] // legacy placeholder is not on this allowlist
    [InlineData(0x80000021u)] // legacy OneDrive tag is not on this allowlist
    [InlineData(0xB000E01Au)] // name-surrogate bit must not be masked out
    [InlineData(0x9001001Au)] // unknown bits outside the cloud variant nibble
    [InlineData(0x0000001Au)] // non-Microsoft tag
    [InlineData(uint.MaxValue)]
    public void LinksAndUnknownReparseTagsAreNeverFollowed(uint tag)
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        File.WriteAllText(Path.Combine(child, "never.txt"), "private synthetic target");
        var fs = new FolderFileSystem();
        fs.Tags[child] = tag;
        var nested = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Empty(nested.Files);
        Assert.Equal(1, nested.SkippedLinks);
        Assert.DoesNotContain(child, fs.Enumerated);
        fs.Tags[root] = tag;
        foreach (bool recursive in new[] { false, true })
        {
            fs.Enumerated.Clear();
            var scan = BatchFolderScanner.Scan(root, recursive, fs, Token);
            Assert.Empty(scan.Files);
            Assert.Equal(1, scan.SkippedLinks);
            Assert.Equal(0, scan.UnreadableFolders);
            Assert.Empty(fs.Enumerated);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UnreadableCloudTagSkipsRootOrChildWithoutStoppingSiblings(int failure)
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(root, "denied")).FullName;
        File.WriteAllText(Path.Combine(root, "kept.txt"), "x");
        var fs = new FolderFileSystem();
        fs.Tags[child] = 0x9000E01A;
        fs.TagFailure = failure switch
        {
            0 => new IOException("Query failed."),
            1 => new UnauthorizedAccessException(),
            2 => new System.Security.SecurityException(),
            _ => new DirectoryNotFoundException()
        };
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Single(scan.Files);
        Assert.Equal(1, scan.UnreadableFolders);
        Assert.Equal(0, scan.SkippedLinks);
        Assert.DoesNotContain(child, fs.Enumerated);
        fs.Tags[root] = 0x9000E01A;
        fs.Enumerated.Clear();
        scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Empty(scan.Files);
        Assert.Equal(1, scan.UnreadableFolders);
        Assert.Empty(fs.Enumerated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloudFolderBecomingLinkOrUnreadableWhileQueuedIsRechecked(bool unreadable)
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        File.WriteAllText(Path.Combine(child, "never.txt"), "x");
        var fs = new FolderFileSystem();
        fs.Tags[child] = 0x9000E01A;
        fs.BeforeTag = path =>
        {
            if (fs.TagReads[path] != 2) return;
            if (unreadable) throw new IOException("The folder disappeared.");
            fs.Tags[path] = 0xA0000003;
        };
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Empty(scan.Files);
        Assert.Equal(unreadable ? 0 : 1, scan.SkippedLinks);
        Assert.Equal(unreadable ? 1 : 0, scan.UnreadableFolders);
        Assert.DoesNotContain(child, fs.Enumerated);
    }

    [Fact]
    public void OrdinaryFolderBecomingLinkWhileQueuedIsRechecked()
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var fs = new FolderFileSystem();
        int reads = 0;
        fs.BeforeAttributes = path => { if (string.Equals(path, child, StringComparison.Ordinal) && ++reads == 2) fs.Tags[path] = 0xA000000C; };
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Equal(1, scan.SkippedLinks);
        Assert.DoesNotContain(child, fs.Enumerated);
    }

    [Theory]
    [InlineData(0)] // before inspection
    [InlineData(1)] // during root tag query
    [InlineData(2)] // after an empty enumeration
    [InlineData(3)] // during a nested tag query
    public void CloudFolderCancellationNeverReturnsASuccessfulPartialScan(int when)
    {
        using var w = new Workspace();
        using var cancellation = new CancellationTokenSource();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        var fs = new FolderFileSystem();
        fs.Tags[root] = 0x9000E01A;
        if (when == 0) cancellation.Cancel();
        if (when == 1) fs.BeforeTag = _ => cancellation.Cancel();
        if (when == 2) fs.BeforeEnumeration = _ => cancellation.Cancel();
        if (when == 3)
        {
            string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            fs.Tags[child] = 0x9000001A;
            fs.BeforeTag = path => { if (string.Equals(path, child, StringComparison.Ordinal)) cancellation.Cancel(); };
        }
        Assert.Throws<OperationCanceledException>(() => BatchFolderScanner.Scan(root, true, fs, cancellation.Token));
        if (when is 0 or 1) Assert.Empty(fs.Enumerated);
    }

    [Fact]
    public void UnavailableCloudEnumerationDoesNotStopReadableSibling()
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string cloud = Directory.CreateDirectory(Path.Combine(root, "1 cloud")).FullName;
        string normal = Directory.CreateDirectory(Path.Combine(root, "2 local")).FullName;
        File.WriteAllText(Path.Combine(normal, "kept.txt"), "x");
        var fs = new FolderFileSystem();
        fs.Tags[cloud] = 0x9000E01A;
        fs.BeforeEnumeration = path => { if (string.Equals(path, cloud, StringComparison.Ordinal)) throw new IOException("Sync provider unavailable."); };
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Single(scan.Files);
        Assert.Equal(1, scan.UnreadableFolders);
    }

    [Fact]
    public async Task CloudInputsImportInNaturalOrderWithoutChangingSourceFiles()
    {
        using var w = new Workspace();
        string root = Directory.CreateDirectory(Path.Combine(w.Root, "input")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "Part 2")).FullName;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Combine(root, "10.txt")] = "ten synthetic words",
            [Path.Combine(root, "2.txt")] = "two synthetic words",
            [Path.Combine(nested, "1.txt")] = "nested synthetic words"
        };
        foreach (var (path, text) in expected) File.WriteAllText(path, text);
        var times = expected.Keys.ToDictionary(path => path, File.GetLastWriteTimeUtc, StringComparer.Ordinal);
        var fs = new FolderFileSystem();
        fs.Tags[root] = fs.Tags[nested] = 0x9000E01A;
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        var sources = scan.Files.Select(path => new BatchSourceFile(Path.GetFileName(path), Sha(File.ReadAllText(path)),
            new FileInfo(path).Length, File.ReadAllText(path))
        { SourcePath = path }).ToArray();
        var items = BatchPlanner.Append([], sources, title => title + ".mp3", _ => false, keepGivenOrder: true);
        var batch = await w.Store.SaveAsync(new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "cloud", 0,
            BatchQueueState.Ready, Settings, Settings.Sha256(), w.Output, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);
        var library = new StrictTitleLibrary();
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(), library, w.Store);
        var texts = items.ToDictionary(item => item.ItemId, item => expected[item.SourcePath!]);
        var imported = await importer.ImportAsync(batch, texts, Token);
        Assert.Equal(["two synthetic words", "ten synthetic words", "nested synthetic words"], library.Created.Select(item => item.Text).ToArray());
        Assert.Equal(3, imported.Items.Count);
        Assert.All(imported.Items, item => Assert.Equal(BatchItemState.Ready, item.State));
        Assert.All(expected, entry =>
        {
            Assert.Equal(entry.Value, File.ReadAllText(entry.Key));
            Assert.Equal(times[entry.Key], File.GetLastWriteTimeUtc(entry.Key));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMetadataRejectsTrueLinksAndMissingTargets(bool junction)
    {
        using var w = new Workspace();
        string target = Directory.CreateDirectory(Path.Combine(w.Root, "target")).FullName;
        string link = Path.Combine(w.Root, "link");
        if (!(junction ? TryJunction(link, target) : TryLink(link, target)))
        { Assert.Skip("Host did not permit this temporary link; no security setting was changed."); return; }
        try
        {
            if (OperatingSystem.IsWindows()) Assert.Equal(junction ? 0xA0000003u : 0xA000000Cu, PhysicalBatchFolderFileSystem.Instance.GetDirectoryReparseTag(link));
            foreach (string root in new[] { link, link + Path.DirectorySeparatorChar })
            {
                var scan = BatchFolderScanner.Scan(root, true, Token);
                Assert.Empty(scan.Files);
                Assert.Equal(1, scan.SkippedLinks);
            }
            Directory.Delete(target); // synthetic empty target only; the link now dangles
            var broken = BatchFolderScanner.Scan(link, true, Token);
            Assert.Empty(broken.Files);
            Assert.Equal(1, broken.SkippedLinks + broken.UnreadableFolders);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void NativeMetadataFailsClosedForMissingOrChangedFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(0u, PhysicalBatchFolderFileSystem.Instance.GetDirectoryReparseTag("unused"));
            return;
        }
        using var w = new Workspace();
        Assert.Throws<IOException>(() => PhysicalBatchFolderFileSystem.Instance.GetDirectoryReparseTag(Path.Combine(w.Root, "missing")));
        Assert.Throws<IOException>(() => PhysicalBatchFolderFileSystem.Instance.GetDirectoryReparseTag(w.Root));
        string file = Path.Combine(w.Root, "file.txt");
        File.WriteAllText(file, "x");
        Assert.Throws<IOException>(() => PhysicalBatchFolderFileSystem.Instance.GetDirectoryReparseTag(file));
    }

    [Fact]
    public void DeepCloudTreesAndLargeFileCountsHaveNoArtificialCutoff()
    {
        var fs = new FolderFileSystem();
        string root = Path.GetFullPath("synthetic-cloud-root");
        string folder = root;
        for (int depth = 0; depth < 1000; depth++)
        {
            string child = folder + Path.DirectorySeparatorChar + "d";
            fs.Attributes[folder] = FileAttributes.Directory | FileAttributes.ReparsePoint;
            fs.Tags[folder] = 0x9000E01A;
            fs.Entries[folder] = [child];
            folder = child;
        }
        fs.Attributes[folder] = FileAttributes.Directory;
        fs.Entries[folder] = Enumerable.Range(1, 10001).Reverse().Select(index =>
            folder + Path.DirectorySeparatorChar + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt").ToArray();
        foreach (string file in fs.Entries[folder]) fs.Attributes[file] = FileAttributes.Archive;
        var scan = BatchFolderScanner.Scan(root, true, fs, Token);
        Assert.Equal(10001, scan.Files.Count);
        Assert.EndsWith("1.txt", scan.Files[0], StringComparison.Ordinal);
        Assert.EndsWith("10001.txt", scan.Files[^1], StringComparison.Ordinal);
        Assert.Equal(1001, fs.Enumerated.Count);
        Assert.Equal(0, scan.SkippedLinks);
        Assert.Equal(0, scan.UnreadableFolders);
    }

    private sealed class FolderFileSystem : IBatchFolderFileSystem
    {
        public Dictionary<string, string[]> Entries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Tags { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FileAttributes> Attributes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TagReads { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Enumerated { get; } = new(StringComparer.Ordinal);
        public Action<string>? BeforeAttributes { get; set; }
        public Action<string>? BeforeTag { get; set; }
        public Action<string>? BeforeEnumeration { get; set; }
        public Exception? TagFailure { get; set; }

        public FileAttributes GetAttributes(string path)
        {
            BeforeAttributes?.Invoke(path);
            if (Attributes.TryGetValue(path, out var value)) return value;
            value = File.GetAttributes(path);
            return Tags.ContainsKey(path) ? value | FileAttributes.ReparsePoint : value;
        }

        public string[] GetEntries(string folder)
        {
            Enumerated.Add(folder);
            BeforeEnumeration?.Invoke(folder);
            return Entries.TryGetValue(folder, out var entries) ? entries : Directory.GetFileSystemEntries(folder);
        }

        public uint GetDirectoryReparseTag(string folder)
        {
            TagReads[folder] = TagReads.GetValueOrDefault(folder) + 1;
            BeforeTag?.Invoke(folder);
            if (TagFailure is { } error) throw error;
            return Tags[folder];
        }
    }
}

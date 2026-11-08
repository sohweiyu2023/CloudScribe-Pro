using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class SupportBundleServiceTests
{
    [Fact]
    public async Task StreamsEveryValidRecordBeyondLegacyCountAndByteCapsWithoutChangingSources()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string source = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-large.jsonl");
        const int count = 6000;
        Guid batch = Guid.NewGuid();
        await using (var writer = new StreamWriter(source))
        {
            for (int i = 0; i < count; i++)
            {
                string line = JsonSerializer.Serialize(new
                {
                    SchemaVersion = 2,
                    EventId = 1643,
                    FieldsComplete = true,
                    Message = PrivateSentinel,
                    SafeFields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["BatchId"] = batch.ToString("D"),
                        ["Order"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["ActiveMilliseconds"] = "100",
                        ["OutputBytes"] = "1000",
                    },
                });
                await writer.WriteLineAsync(line.AsMemory(), TestContext.Current.CancellationToken);
            }
        }
        Assert.True(new FileInfo(source).Length > 1024 * 1024);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        string bundle = await CreateService(paths, 1).CreateAsync(Path.Combine(temporary.Path, "exports"), TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(bundle);
        var log = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal));
        Assert.True(log.Length > 1024 * 1024);
        using var reader = new StreamReader(log.Open());
        int observed = 0;
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            Assert.DoesNotContain(PrivateSentinel, line, StringComparison.Ordinal);
            using var row = JsonDocument.Parse(line);
            Assert.True(row.RootElement.GetProperty("FieldsComplete").GetBoolean());
            Assert.Equal(observed.ToString(System.Globalization.CultureInfo.InvariantCulture), row.RootElement.GetProperty("SafeFields").GetProperty("Order").GetString());
            observed++;
        }
        Assert.Equal(count, observed);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(temporary.Path, "exports")), file => file.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportsPermanentTrendAfterRoutineLogRemovalWithSafeGapsAndNoPrivateHashes()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string history = Path.Combine(paths.RootDirectory, "data", "google-trend-history-v1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        await File.WriteAllTextAsync(history, "{PRIVATE_INTERRUPTED_TAIL", TestContext.Current.CancellationToken);
        Guid batch = Guid.NewGuid(), item = Guid.NewGuid(), job = Guid.NewGuid();
        string idempotencyKeySha256 = new('a', 64), payloadSha256 = new('b', 64);
        using (var provider = new GoogleTrendHistoryLoggerProvider(paths, TimeProvider.System))
        {
            var logger = provider.CreateLogger(PrivateSentinel);
            CloudScribeLog.BatchItemJobLinked(logger, batch, item, 1, job);
            CloudScribeLog.ArticleRequestIdentity(logger, job, 1, idempotencyKeySha256, payloadSha256);
        }
        await File.AppendAllTextAsync(history, JsonSerializer.Serialize(new { Version = 1, Kind = "Event", EventId = 9999, Message = PrivateSentinel }) + "\n", TestContext.Current.CancellationToken);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(history, TestContext.Current.CancellationToken));
        var service = CreateService(paths, 1);
        var preview = await service.PreviewAsync(TestContext.Current.CancellationToken);
        Assert.Contains(preview.Files, file => string.Equals(file.RelativePath, "history/google-trend-history.jsonl", StringComparison.Ordinal));
        string bundle = await service.CreateAsync(Path.Combine(temporary.Path, "exports"), TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(bundle);
        var entry = Assert.Single(archive.Entries, file => string.Equals(file.FullName, "history/google-trend-history.jsonl", StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open());
        string text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("TrendGap", text, StringComparison.Ordinal);
        Assert.Contains("TrendSession", text, StringComparison.Ordinal);
        Assert.Contains("BatchItemJobLinked", text, StringComparison.Ordinal);
        Assert.Contains(job.ToString("D"), text, StringComparison.Ordinal);
        Assert.Contains("MalformedRecord", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateSentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(idempotencyKeySha256, text, StringComparison.Ordinal);
        Assert.DoesNotContain(payloadSha256, text, StringComparison.Ordinal);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(history, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ExportsAllLargeTrendEventsWithNoRoutineLogs()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string history = Path.Combine(paths.RootDirectory, "data", "google-trend-history-v1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        Guid batch = Guid.NewGuid();
        const int count = 6000;
        await using (var writer = new StreamWriter(history))
        {
            for (int i = 0; i < count; i++)
            {
                string row = JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Kind = "Event",
                    TimestampUtc = DateTimeOffset.UnixEpoch,
                    EventId = 1643,
                    FieldsComplete = true,
                    Module = PrivateSentinel,
                    ApplicationVersion = "1.0.3",
                    SafeFields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["BatchId"] = batch.ToString("D"),
                        ["Order"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["ActiveMilliseconds"] = "1",
                        ["OutputBytes"] = "2",
                    },
                });
                await writer.WriteLineAsync(row.AsMemory(), TestContext.Current.CancellationToken);
            }
        }
        Assert.True(new FileInfo(history).Length > 1024 * 1024);
        string bundle = await CreateService(paths, 1).CreateAsync(Path.Combine(temporary.Path, "exports"), TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(bundle);
        using var reader = new StreamReader(archive.Entries.Single(x => x.FullName.StartsWith("history/", StringComparison.Ordinal)).Open());
        int observed = 0;
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            Assert.DoesNotContain(PrivateSentinel, line, StringComparison.Ordinal);
            using var row = JsonDocument.Parse(line);
            Assert.Equal("TrendMetadata", row.RootElement.GetProperty("RecordState").GetString());
            Assert.True(row.RootElement.GetProperty("FieldsComplete").GetBoolean());
            observed++;
        }
        Assert.Equal(count, observed);
    }

    [Fact]
    public async Task CancellationDuringStreamingRemovesPartialArchive()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string source = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-cancel.jsonl");
        // A sparse source gives the worker ample streaming work without retaining it in memory.
        using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write)) file.SetLength(128L * 1024 * 1024);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string destination = Path.Combine(temporary.Path, "exports");
        Task<string> export = Task.Run(() => CreateService(paths, 1).CreateAsync(destination, cancellation.Token), TestContext.Current.CancellationToken);
        while (!export.IsCompleted && (!Directory.Exists(destination) || !Directory.EnumerateFiles(destination, "*.partial").Any()))
            await Task.Delay(1, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.True(File.Exists(source));
        Assert.True(!Directory.Exists(destination) || !Directory.EnumerateFiles(destination).Any());
    }

    [Fact]
    public async Task SourceReadFailureDoesNotPublishOrLeaveAPartialArchive()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string source = Path.Combine(paths.DiagnosticsDirectory, "cloudscribe-locked.jsonl");
        await File.WriteAllTextAsync(source, "{}\n", TestContext.Current.CancellationToken);
        using var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string destination = Path.Combine(temporary.Path, "exports");
        await Assert.ThrowsAnyAsync<IOException>(() => CreateService(paths, 1).CreateAsync(destination, TestContext.Current.CancellationToken));
        Assert.True(!Directory.Exists(destination) || !Directory.EnumerateFiles(destination).Any());
        Assert.Equal(3, locked.Length);
    }

    [Fact]
    public async Task LinkedTrendDirectoryIsRefusedWhenSupported()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string directory = Path.Combine(paths.RootDirectory, "data");
        if (Directory.Exists(directory)) Directory.Delete(directory);
        string outside = Path.Combine(temporary.Path, "outside-trend"); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "google-trend-history-v1.jsonl"), PrivateSentinel, TestContext.Current.CancellationToken);
        if (!TryCreateDirectorySymbolicLink(directory, outside))
        {
            Assert.Skip("Host could not create this temporary directory-link fixture; no privilege or security change attempted.");
            return;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(paths, 1).PreviewAsync(TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "exports")));
    }

    [Fact]
    public async Task LinkedTrendFileIsRefusedWithoutReadingItsContentsWhenSupported()
    {
        await using TemporaryDirectory temporary = new();
        var paths = CreatePaths(temporary.Path); paths.EnsureDirectories();
        string directory = Path.Combine(paths.RootDirectory, "data"); Directory.CreateDirectory(directory);
        string outside = Path.Combine(temporary.Path, "private.jsonl");
        await File.WriteAllTextAsync(outside, PrivateSentinel, TestContext.Current.CancellationToken);
        if (!TryCreateFileSymbolicLink(Path.Combine(directory, "google-trend-history-v1.jsonl"), outside))
        {
            Assert.Skip("Host could not create this temporary file-link fixture; no privilege or security change attempted.");
            return;
        }
        var service = CreateService(paths, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Path.Combine(temporary.Path, "exports"), TestContext.Current.CancellationToken));
        Assert.Equal(PrivateSentinel, await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "exports")));
    }
}

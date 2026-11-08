#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Fact]
    public async Task CorruptPrimaryFallbackAndPreservationHaveSafeStoreEvidence()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["PRIVATE_SOURCE.txt"]);
        await w.Store.SaveAsync(batch with { Name = "second" }, Token);
        string path = Path.Combine(w.Store.Directory, batch.BatchId.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{PRIVATE_CORRUPT_CONTENT", Token);
        var log = new ListLogger();
        using var store = new FileBatchQueueStore(Path.Combine(w.Root, "profile"), log);
        var recovered = await store.LoadAsync(batch.BatchId, Token);
        Assert.NotNull(recovered);
        await store.SaveAsync(recovered, Token);
        Assert.Contains(log.Messages, message => message.Contains("ReadPrimary failed; category MalformedData", StringComparison.Ordinal));
        Assert.Contains(log.Messages, message => message.Contains("LoadedBackup", StringComparison.Ordinal));
        Assert.Contains(log.Messages, message => message.Contains("DamagedPrimaryPreserved", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("PRIVATE_", StringComparison.Ordinal) || message.Contains(w.Root, StringComparison.Ordinal));
        Assert.Equal("{PRIVATE_CORRUPT_CONTENT", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(w.Store.Directory, "*.damaged-*")), Token));
    }

    [Theory]
    [InlineData("", "Empty")]
    [InlineData("null", "Unsupported")]
    [InlineData("{PRIVATE_INVALID", "MalformedData")]
    public async Task UnreadableQueuesReportTheReasonWithoutPrivateFileContents(string content, string reason)
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        await File.WriteAllTextAsync(Path.Combine(w.Store.Directory, batch.BatchId.ToString("N") + ".json"), content, Token);
        var log = new ListLogger();
        using var store = new FileBatchQueueStore(Path.Combine(w.Root, "profile"), log);
        Assert.Null(await store.LoadAsync(batch.BatchId, Token));
        Assert.Contains(log.Messages, message => message.Contains(reason, StringComparison.Ordinal));
        Assert.Contains(log.Messages, message => message.Contains("NoReadableQueue", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("PRIVATE_INVALID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveFailureAndConcurrencyRetainOriginalExceptionsAndLogFixedCategories()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var log = new ListLogger();
        using var store = new FileBatchQueueStore(Path.Combine(w.Root, "profile"), log);
        await store.SaveAsync(batch, Token);
        await Assert.ThrowsAsync<BatchConcurrencyException>(() => store.SaveAsync(batch, Token));
        Assert.Contains(log.Messages, message => message.Contains("Save failed; category Concurrency", StringComparison.Ordinal));
        string blocked = Path.Combine(w.Root, "occupied");
        await File.WriteAllTextAsync(blocked, "PRIVATE_STORAGE_CONTENT", Token);
        using var failing = new FileBatchQueueStore(blocked, log);
        await Assert.ThrowsAnyAsync<IOException>(() => failing.SaveAsync(batch, Token));
        Assert.Contains(log.Messages, message => message.Contains("Save failed; category", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains(blocked, StringComparison.Ordinal) || message.Contains("PRIVATE_STORAGE_CONTENT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedNarrationFailureLogsSystemCodeWithoutExceptionTextAndNeverRetries()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var narrator = new FakeNarrator();
        narrator.Behaviour[1] = (_, _) => throw new IOException("PRIVATE_PROVIDER_AND_SOURCE_TEXT", unchecked((int)0x80070070));
        var log = new ListLogger();
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System, log).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Uncertain, result.Items[0].State);
        Assert.Single(narrator.Calls);
        Assert.Contains(log.Messages, message => message.Contains("Narrate failed; category Storage; numeric error -2147024784", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("PRIVATE_PROVIDER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreRunAuthorityFailureLogsFixedCategoryAndKeepsSavedQueue()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var narrator = new FakeNarrator { Authorized = false };
        var log = new ListLogger();
        await Assert.ThrowsAsync<BatchNotAuthorizedException>(() => new BatchQueueRunner(w.Store, narrator, TimeProvider.System, log)
            .RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token));
        Assert.Contains(log.Messages, message => message.Contains("Run failed; category NotAuthorized", StringComparison.Ordinal));
        Assert.Empty(narrator.Calls);
        Assert.NotNull(await w.Store.LoadAsync(batch.BatchId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreLoggingFailureCannotPreventSaveOrFallback(bool failAtEnabledCheck)
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        using var store = new FileBatchQueueStore(Path.Combine(w.Root, "profile"), new ThrowingBatchLogger(failAtEnabledCheck));
        var saved = await store.SaveAsync(batch, Token);
        Assert.Equal(batch.Revision + 1, saved.Revision);
        await File.WriteAllTextAsync(Path.Combine(store.Directory, batch.BatchId.ToString("N") + ".json"), "invalid", Token);
        Assert.NotNull(await store.LoadAsync(batch.BatchId, Token));
    }
}

#pragma warning disable MA0004 // Synthetic tests have no synchronization context to preserve.
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Fact]
    public async Task RealBatchImportSurvivesQueueSaveFailureWithDistinctRawOriginalAndNarrationPins()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var queueStore = new FileBatchQueueStore(fixture.Paths.RootDirectory);
        var failingStore = new FailFirstQueueSave(queueStore);
        var importer = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(),
            fixture.Library, failingStore, narrationCopies: fixture.Store());
        byte[] raw = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(SourceText)];
        using var bytes = new MemoryStream(raw);
        var read = await importer.ReadAsync("1 input.txt", bytes, 1024 * 1024, Token);
        var files = new[] { read, new BatchSourceFile("2 only URLs.txt", Hash("https://example.test"), 20, "https://example.test"),
            new BatchSourceFile("3 plain.txt", Hash("Plain text."), 11, "Plain text.") };
        var items = BatchPlanner.Plan(files, title => title + ".mp3", _ => false);
        var settings = new BatchSettings("en-US", "synthetic-voice", "Synthetic voice", "complete-article",
            BatchFailurePolicy.SkipAndContinue, true, NarrationUrlOmission.CurrentVersion);
        var batch = await queueStore.SaveAsync(new(NarrationBatch.CurrentVersion, Guid.NewGuid(), "Synthetic", 0,
            BatchQueueState.Ready, settings, settings.Sha256(), fixture.Paths.RootDirectory, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);
        var text = items.ToDictionary(item => item.ItemId,
            item => files.Single(file => string.Equals(file.FileName, item.SourceFileName, StringComparison.Ordinal)).Text!);
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(batch, text, Token));
        using var db = fixture.Factory.CreateDbContext();
        var committedCopy = await db.Set<NarrationCopyEntity>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(2, await db.Documents.CountAsync(Token));
        var pending = Assert.IsType<NarrationBatch>(await queueStore.LoadAsync(batch.BatchId, Token));
        Assert.All(pending.Items, item => Assert.Null(item.DocumentId));
        var restarted = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(),
            fixture.Library, queueStore, narrationCopies: fixture.Store());
        var imported = await restarted.ImportAsync(pending, text, Token);
        var spoken = imported.Items[0];
        Assert.Equal(committedCopy.ChildDocumentId, spoken.DocumentId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), spoken.SourceSha256);
        Assert.Equal(Hash(SourceText.Replace("\r\n", "\n", StringComparison.Ordinal)), spoken.NarrationSource!.TextSha256);
        Assert.NotEqual(spoken.SourceSha256, spoken.NarrationSource.TextSha256, StringComparer.Ordinal);
        Assert.NotEqual(spoken.NarrationSource.TextSha256, spoken.NarrationTextSha256, StringComparer.Ordinal);
        var narration = Assert.IsType<DocumentSnapshot>(await fixture.Library.OpenAsync(spoken.DocumentId!.Value, Token));
        Assert.Equal(Hash(narration.Text), spoken.NarrationTextSha256);
        Assert.Equal(BatchItemState.Excluded, imported.Items[1].State);
        Assert.Equal(BatchExclusion.NoSpeakableText, imported.Items[1].Exclusion);
        Assert.Null(imported.Items[2].NarrationSource);
        await BatchNarrationIdentity.ValidateAsync(fixture.Library, settings, imported.Items[2], fixture.Store(), Token);
        Assert.Equal(4, await db.Documents.CountAsync(Token));
        Assert.Equal(3, await db.Set<BatchSourceImportEntity>().CountAsync(Token));
        Assert.Single(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
        Assert.Equal(4, fixture.Blobs().Length);
    }

    private sealed class FailFirstQueueSave(IBatchQueueStore inner) : IBatchQueueStore
    {
        private bool _failed;
        public Task<NarrationBatch?> LoadAsync(Guid batchId, CancellationToken cancellationToken = default) => inner.LoadAsync(batchId, cancellationToken);
        public Task<IReadOnlyList<NarrationBatch>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public IDisposable? TryAcquireRunLock(Guid batchId) => inner.TryAcquireRunLock(batchId);
        public Task<NarrationBatch> SaveAsync(NarrationBatch queue, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("Synthetic queue save interrupted.");
            }
            return inner.SaveAsync(queue, cancellationToken);
        }
    }
}

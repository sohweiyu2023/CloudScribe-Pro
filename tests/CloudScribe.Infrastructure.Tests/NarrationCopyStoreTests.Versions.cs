#pragma warning disable MA0004 // Synthetic tests have no synchronization context to preserve.
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
    public async Task ImmutableVersionsCreateDistinctCopiesAndReplayExactPriorTextAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync("Read 84000.co and https://example.org then finish.");
        var old = await fixture.Store().PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash(source.Text), NarrationUrlOmission.LegacyVersion, Token);
        var current = await PrepareAsync(fixture.Store(), source);
        Assert.Equal("Read 84000.co and  then finish.", old.Narration.Text);
        Assert.Equal("Read  and  then finish.", current.Narration.Text);
        Assert.NotEqual(old.Narration.Id, current.Narration.Id);
        Assert.NotEqual(old.Narration.CurrentRevisionId, current.Narration.CurrentRevisionId);
        Assert.Equal(old, await fixture.Store().FindAsync(old.Narration.Id, Token));
        Assert.Equal(current, await fixture.Store().FindAsync(current.Narration.Id, Token));
        Assert.Equal(old, await fixture.Store().PrepareAsync(source.Id, source.CurrentRevisionId.Value, Hash(source.Text), NarrationUrlOmission.LegacyVersion, Token));
        Assert.Equal(current, await PrepareAsync(fixture.Store(), source));
        Assert.Equal(source, await fixture.Library.OpenAsync(source.Id, Token));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(3, await db.Documents.CountAsync(Token));
        Assert.Equal(2, await db.Set<NarrationCopyEntity>().CountAsync(Token));
    }

    [Theory]
    [InlineData("url-omission-v1")]
    [InlineData("url-omission-v2")]
    public async Task InterruptedImportReusesPinnedVersionWithoutUpgrading(string version)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var queueStore = new FileBatchQueueStore(fixture.Paths.RootDirectory);
        const string text = "Read 84000.co and https://example.org now.";
        var items = BatchPlanner.Plan([new BatchSourceFile("input.txt", Hash(text), text.Length, text)], title => title + ".mp3", _ => false);
        var settings = new BatchSettings("en-US", "synthetic-voice", "Synthetic voice", "complete-article",
            BatchFailurePolicy.SkipAndContinue, true, version);
        var batch = await queueStore.SaveAsync(new(NarrationBatch.CurrentVersion, Guid.NewGuid(), "Synthetic", 0,
            BatchQueueState.Ready, settings, settings.Sha256(), fixture.Paths.RootDirectory, "USD", 2, items, DateTimeOffset.UnixEpoch), Token);
        var texts = new Dictionary<Guid, string> { [items[0].ItemId] = text };
        var interrupted = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(),
            fixture.Library, new FailFirstQueueSave(queueStore), narrationCopies: fixture.Store());
        await Assert.ThrowsAsync<IOException>(() => interrupted.ImportAsync(batch, texts, Token));
        using var db = fixture.Factory.CreateDbContext();
        var first = await db.Set<NarrationCopyEntity>().AsNoTracking().SingleAsync(Token);
        var restarted = new BatchDocumentImporter(new BoundedLocalDocumentImporter(), new DocumentPreprocessor(),
            fixture.Library, queueStore, narrationCopies: fixture.Store());
        var pending = Assert.IsType<NarrationBatch>(await queueStore.LoadAsync(batch.BatchId, Token));
        var result = await restarted.ImportAsync(pending, texts, Token);
        var item = Assert.Single(result.Items);
        Assert.Equal(first.ChildDocumentId, item.DocumentId);
        Assert.Equal(version, item.NarrationSource!.TransformVersion);
        Assert.Equal(version, result.Settings.UrlTransformVersion);
        Assert.Equal(batch.SettingsSha256, result.SettingsSha256);
        var copy = Assert.IsType<NarrationCopyPreparation>(await fixture.Store().FindAsync(item.DocumentId!.Value, Token));
        Assert.Equal(NarrationUrlOmission.Transform(text, version).OutputText, copy.Narration.Text);
        await BatchNarrationIdentity.ValidateAsync(fixture.Library, settings, item, fixture.Store(), Token);
        Assert.Equal(2, await db.Documents.CountAsync(Token));
        Assert.Single(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
    }

    [Fact]
    public async Task LegacyNoOmissionIdentityStaysValidButCannotBeRelabeledCurrent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync("Read 84000.co now.");
        var old = await fixture.Store().PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash(source.Text), NarrationUrlOmission.LegacyVersion, Token);
        Assert.Equal(source.Id, old.Narration.Id);
        Assert.Equal(0, old.OmittedUrlCount);
        var item = new BatchItem(Guid.NewGuid(), 1, "input.txt", Hash(source.Text), source.Text.Length, source.Text.Length, "input.mp3", BatchItemState.Ready,
            DocumentId: source.Id, RevisionId: source.CurrentRevisionId, NarrationTextSha256: Hash(source.Text));
        var settings = new BatchSettings("en-US", "synthetic-voice", "Synthetic voice", "complete-article",
            BatchFailurePolicy.SkipAndContinue, true, NarrationUrlOmission.LegacyVersion);
        await BatchNarrationIdentity.ValidateAsync(fixture.Library, settings, item, fixture.Store(), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BatchNarrationIdentity.ValidateAsync(fixture.Library,
            settings with { UrlTransformVersion = NarrationUrlOmission.CurrentVersion }, item, fixture.Store(), Token));
        Assert.Equal(source, await fixture.Library.OpenAsync(source.Id, Token));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("url-omission-v2")]
    public async Task AlteredLegacyProvenanceVersionFailsClosed(string changedVersion)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        var old = await fixture.Store().PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash(source.Text), NarrationUrlOmission.LegacyVersion, Token);
        using (var db = fixture.Factory.CreateDbContext())
        {
            var row = await db.Set<NarrationCopyEntity>().SingleAsync(Token);
            // Corruption fixture only: remove the immutable trigger in this disposable synthetic database.
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER narration_copies_immutable;", Token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE narration_copies SET TransformVersion = {changedVersion} WHERE ChildDocumentId = {row.ChildDocumentId}", Token);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().FindAsync(old.Narration.Id, Token));
    }
}

#pragma warning disable MA0004 // Synthetic tests have no synchronization context to preserve.
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Theory]
    [InlineData(false, "revision")]
    [InlineData(true, "revision")]
    [InlineData(false, "archived")]
    [InlineData(true, "archived")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "draft")]
    [InlineData(true, "draft")]
    [InlineData(false, "hash")]
    [InlineData(true, "hash")]
    [InlineData(false, "content")]
    [InlineData(true, "content")]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "metadata-both")]
    [InlineData(true, "metadata-both")]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    public async Task StaleOrCorruptSourceAndChildFailClosedOnPreparationAndLookup(bool child, string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        var prepared = await PrepareAsync(fixture.Store(), source);
        var target = child ? prepared.Narration : source;
        await MutateAsync(fixture, target, mutation);
        if (mutation is "revision" or "archived" or "deleted")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync(fixture.Store(), source));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().FindAsync(prepared.Narration.Id, Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAsync(fixture.Store(), source));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store().FindAsync(prepared.Narration.Id, Token));
        }
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await db.Documents.CountAsync(Token));
        Assert.Single(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
    }

    [Fact]
    public async Task IncorrectSourceHashAndRevisionAreRejectedBeforeAnyCopy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store().PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash("different"), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().PrepareAsync(source.Id, Guid.NewGuid(), Hash(source.Text), Token));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Single(fixture.Blobs());
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("archived")]
    [InlineData("deleted")]
    [InlineData("hash")]
    [InlineData("file")]
    public async Task ChangedImportedSourceIsNotSilentlyReimported(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid batchId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var source = await fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token);
        await MutateAsync(fixture, source, mutation);
        if (mutation is "revision" or "archived" or "deleted")
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Single(await db.Set<BatchSourceImportEntity>().ToArrayAsync(Token));
    }

    [Fact]
    public async Task CancellationNeverPublishesACopy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store().PrepareAsync(
            source.Id, source.CurrentRevisionId!.Value, Hash(source.Text), cancellation.Token));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
        Assert.Single(fixture.Blobs());
    }

    private static async Task MutateAsync(Fixture fixture, DocumentSnapshot target, string mutation)
    {
        if (mutation is "revision")
        {
            await fixture.Library.SaveAsync(new(target.Id, target.Title, target.Text,
                target.ConcurrencyVersion, DocumentRevisionKind.Checkpoint), Token);
            return;
        }
        if (mutation is "archived" or "deleted")
        {
            await fixture.Library.ChangeStatusAsync(target.Id, mutation is "archived"
                ? DocumentStatus.Archived : DocumentStatus.Deleted, target.ConcurrencyVersion, Token);
            return;
        }
        using var db = fixture.Factory.CreateDbContext();
        var document = await db.Documents.SingleAsync(row => row.Id == target.Id, Token);
        var revision = await db.DocumentRevisions.SingleAsync(row => row.Id == target.CurrentRevisionId, Token);
        switch (mutation)
        {
            case "draft": document.DraftText += "changed"; break;
            case "hash": revision.ContentSha256 = Hash("changed"); break;
            case "content": revision.ContentText += "changed"; break;
            case "metadata": revision.ContentByteLength = null; break;
            case "metadata-both": revision.ContentRelativePath = null; revision.ContentByteLength = null; break;
            case "file":
                await File.WriteAllTextAsync(Path.Combine(fixture.Paths.RootDirectory, revision.ContentRelativePath!), "corrupt", Token);
                break;
            default: throw new ArgumentException("Unknown test mutation.", nameof(mutation));
        }
        await db.SaveChangesAsync(Token);
    }
}

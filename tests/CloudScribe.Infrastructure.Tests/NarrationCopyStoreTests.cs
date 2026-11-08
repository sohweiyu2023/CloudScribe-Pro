#pragma warning disable MA0004 // Synthetic tests have no synchronization context to preserve.
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Fact]
    public async Task PreparationIsExactDurableAndLeavesOriginalRevisionAndBytesUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        string sourceFile = Assert.Single(fixture.Blobs());
        byte[] original = await File.ReadAllBytesAsync(sourceFile, Token);
        var prepared = await PrepareAsync(fixture.Store(), source);
        Assert.NotEqual(source.Id, prepared.Narration.Id);
        Assert.Equal(NarrationUrlOmission.Transform(SourceText).OutputText, prepared.Narration.Text);
        Assert.Equal(2, prepared.OmittedUrlCount);
        Assert.Equal(source, await fixture.Library.OpenAsync(source.Id, Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(sourceFile, Token));
        using var db = fixture.Factory.CreateDbContext();
        var provenance = await db.Set<NarrationCopyEntity>().SingleAsync(Token);
        Assert.Equal(source.Id, provenance.SourceDocumentId);
        Assert.Equal(source.CurrentRevisionId, provenance.SourceRevisionId);
        Assert.Equal(Hash(source.Text), provenance.SourceTextSha256);
        Assert.Equal(prepared.Narration.Id, provenance.ChildDocumentId);
        Assert.Equal(prepared.Narration.CurrentRevisionId, provenance.ChildRevisionId);
        Assert.Equal(Hash(prepared.Narration.Text), provenance.ChildTextSha256);
        Assert.Equal(NarrationUrlOmission.CurrentVersion, provenance.TransformVersion);
        Assert.Equal("skip-urls=true", provenance.TransformSettings);
        Assert.Equal(2, provenance.OmittedUrlCount);
        Assert.Equal(2, await db.Documents.CountAsync(Token));
        Assert.Equal(2, await db.DocumentRevisions.CountAsync(Token));
        Assert.Empty(await db.Set<NarrationPartGroupEntity>().ToArrayAsync(Token));
    }

    [Fact]
    public async Task RepeatedClicksRestartAndUnrecordedQueueSaveReuseTheExactCopy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        var first = await PrepareAsync(fixture.Store(), source);
        // The caller never saved its queue. A fresh store must still find the committed preparation.
        var second = await PrepareAsync(fixture.Store(), source);
        Assert.Equal(first, second);
        Assert.Equal(first, await fixture.Store().FindAsync(first.Narration.Id, Token));
        Assert.Null(await fixture.Store().FindAsync(source.Id, Token));
        Assert.Equal(2, fixture.Blobs().Length);
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
    }

    [Theory]
    [InlineData("Text with no URL\r\nNo cleanup.")]
    [InlineData("")]
    [InlineData("...")]
    public async Task NoUrlReturnsOriginalWithoutAnyUnnecessaryCopy(string text)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync(text);
        var prepared = await PrepareAsync(fixture.Store(), source);
        Assert.Equal(source, prepared.Narration);
        Assert.Equal(0, prepared.OmittedUrlCount);
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Empty(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
        Assert.Single(fixture.Blobs());
    }

    [Theory]
    [InlineData("https://example.test")]
    [InlineData(" (https://example.test), www.example.test! ")]
    public async Task UrlOnlyRefusesWithoutPublishingAnything(string text)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync(text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync(fixture.Store(), source));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Empty(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
        Assert.Single(fixture.Blobs());
    }

    [Fact]
    public async Task BatchImportIsExactAtomicAndRestartReusable()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid batchId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        string rawHash = Hash("original encoded input");
        var first = await fixture.Store().ImportBatchSourceAsync(batchId, itemId, rawHash, "Input", SourceText, Token);
        var repeated = await fixture.Store().ImportBatchSourceAsync(batchId, itemId, rawHash, "Input", SourceText, Token);
        Assert.Equal(first, repeated);
        Assert.Equal(SourceText, first.Text);
        using var db = fixture.Factory.CreateDbContext();
        var pin = await db.Set<BatchSourceImportEntity>().SingleAsync(Token);
        Assert.Equal(batchId, pin.BatchId);
        Assert.Equal(itemId, pin.ItemId);
        Assert.Equal(rawHash, pin.RawSha256);
        Assert.Equal(Hash(SourceText), pin.SourceTextSha256);
        Assert.Equal(first.Id, pin.SourceDocumentId);
        Assert.Equal(first.CurrentRevisionId, pin.SourceRevisionId);
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Single(await db.DocumentRevisions.ToArrayAsync(Token));
        Assert.Single(fixture.Blobs());
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("text")]
    [InlineData("title")]
    public async Task DuplicateBatchItemWithChangedPinFailsClosed(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid batchId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        string rawHash = Hash("raw");
        await fixture.Store().ImportBatchSourceAsync(batchId, itemId, rawHash, "Input", SourceText, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().ImportBatchSourceAsync(
            batchId, itemId, change is "raw" ? Hash("other") : rawHash,
            change is "title" ? "Other" : "Input", change is "text" ? SourceText + "other" : SourceText, Token));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Single(fixture.Blobs());
    }
}

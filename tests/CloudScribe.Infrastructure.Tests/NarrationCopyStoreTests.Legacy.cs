using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Fact]
    public async Task ImportedSourceCannotLoseFileMetadataBeforeItsFirstPreparation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.Store().ImportBatchSourceAsync(Guid.NewGuid(), Guid.NewGuid(), Hash("raw"), "Input", SourceText, Token);
        await MutateAsync(fixture, source, "metadata-both");
        await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAsync(fixture.Store(), source));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
        Assert.Single(await db.Documents.ToArrayAsync(Token));
    }

    [Fact]
    public async Task LegacyDatabaseOnlySourceRetainsItsVerifiedContentWithoutRequiringANewSourceRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid documentId = Guid.NewGuid();
        Guid revisionId = Guid.NewGuid();
        using var db = fixture.Factory.CreateDbContext();
        db.Documents.Add(new()
        {
            Id = documentId,
            CurrentRevisionId = revisionId,
            Title = "Legacy",
            DraftText = SourceText,
            ConcurrencyVersion = 1,
            Status = 0,
            CreatedAtUnixMilliseconds = 1,
            UpdatedAtUnixMilliseconds = 1,
        });
        db.DocumentRevisions.Add(new()
        {
            Id = revisionId,
            DocumentId = documentId,
            ContentText = SourceText,
            ContentSha256 = Hash(SourceText),
            CreatedAtUnixMilliseconds = 1,
        });
        await db.SaveChangesAsync(Token);
        var source = Assert.IsType<DocumentSnapshot>(await fixture.Library.OpenAsync(documentId, Token));
        var prepared = await PrepareAsync(fixture.Store(), source);
        Assert.Equal(prepared, await fixture.Store().FindAsync(prepared.Narration.Id, Token));
        Assert.Equal(source, await fixture.Library.OpenAsync(documentId, Token));
        var pin = await db.Set<NarrationCopyEntity>().AsNoTracking().SingleAsync(Token);
        Assert.Null(pin.SourceContentRelativePath);
        Assert.Null(pin.SourceContentByteLength);
        Assert.Single(fixture.Blobs());
    }
}

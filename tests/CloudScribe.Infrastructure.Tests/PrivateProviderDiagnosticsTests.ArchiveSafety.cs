using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public void ArticleRecordWithoutProtectedResponseIsRejectedBeforeAnyFilesystemWrite()
    {
        using var f = new Fixture(); var persistence = PoolStore(f.Root, f.Clock);
        var record = PoolRecord(f.Clock.GetUtcNow(), false) with
        { ArticleJobId = Guid.NewGuid(), ArticleOrdinal = 1, Response = null };
        Assert.Throws<InvalidDataException>(() => persistence.Save(record));
        Assert.False(Directory.Exists(f.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroLengthNamedArchiveFailsProtectedCapturePreflight(bool article)
    {
        using var f = new Fixture(); var persistence = PoolStore(f.Root, f.Clock);
        var record = PoolRecord(f.Clock.GetUtcNow(), false);
        if (article) record = record with { ArticleJobId = Guid.NewGuid(), ArticleOrdinal = 1 };
        Assert.True(persistence.Save(record));
        string path = Assert.Single(Directory.GetFiles(f.Root));
        using (var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None)) { }
        Assert.False(persistence.PrepareProtectedCapture());
        Assert.Equal(PrivateProviderErrorReadStatus.Failed, persistence.ReadWithStatus(record.Id).Status);
    }
}

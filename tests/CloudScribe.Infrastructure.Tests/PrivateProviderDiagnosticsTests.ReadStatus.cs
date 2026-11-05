using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task CorruptProtectedStoreIsNotReportedAsSuccessfulEmptyRead()
    {
        using var f = new Fixture(); ArmEnvelope(f); await f.SendAsync("Unique synthetic explanation");
        Assert.Single(f.Store.ReadRecords()); Assert.Equal(PrivateProviderErrorReadStatus.Success, f.Store.ReadStatus);
        string path = Assert.Single(Directory.GetFiles(f.Root));
        File.WriteAllBytes(path, "{\"Message\":\"plaintext replacement\"}"u8.ToArray());
        using var restarted = new GooglePrivateErrorDiagnostics(f.Root, f.Clock);
        Assert.Empty(await restarted.ReadRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PrivateProviderErrorReadStatus.CorruptOrUnprotectable, restarted.ReadStatus);
        Assert.True(File.Exists(path)); // No fallback, plaintext interpretation or unapproved evidence deletion.
    }

    [Fact]
    public async Task SuccessfulEmptyExpiredAndDisposedReadsHaveDistinctHealth()
    {
        using var f = new Fixture();
        Assert.Empty(await f.Store.ReadRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PrivateProviderErrorReadStatus.Success, f.Store.ReadStatus);
        ArmEnvelope(f); await f.SendAsync("Synthetic expiry evidence");
        f.Clock.Now += TimeSpan.FromHours(24);
        Assert.Empty(await f.Store.ReadRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PrivateProviderErrorReadStatus.Success, f.Store.ReadStatus);
        f.Store.Dispose();
        Assert.Empty(await f.Store.ReadRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PrivateProviderErrorReadStatus.Disposed, f.Store.ReadStatus);
    }
}

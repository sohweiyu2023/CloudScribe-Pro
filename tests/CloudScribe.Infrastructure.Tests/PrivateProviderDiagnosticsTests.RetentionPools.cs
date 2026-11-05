using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public void SuccessMetadataCannotEvictProtectedErrorExplanation()
    {
        using var f = new Fixture();
        var persistence = PoolStore(f.Root, f.Clock);
        var error = PoolRecord(f.Clock.GetUtcNow(), false);
        Assert.True(persistence.Save(error));
        for (int i = 0; i < 20; i++)
        {
            f.Clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(persistence.Save(PoolRecord(f.Clock.GetUtcNow(), true)));
        }
        var restarted = PoolStore(f.Root, f.Clock).ReadWithStatus();
        Assert.Equal(PrivateProviderErrorReadStatus.Success, restarted.Status);
        Assert.Equal(9, restarted.Records.Count);
        Assert.Contains(restarted.Records, x => x.Id == error.Id && string.Equals(x.Response!.RedactedBody, "Unique retained explanation", StringComparison.Ordinal));
        for (int i = 0; i < 10; i++)
        {
            f.Clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(persistence.Save(PoolRecord(f.Clock.GetUtcNow(), false)));
        }
        var rotated = persistence.ReadWithStatus();
        Assert.Equal(PrivateProviderErrorReadStatus.Success, rotated.Status);
        Assert.Equal(19, rotated.Records.Count);
        Assert.Equal(11, rotated.Records.Count(x => x.Response!.HttpStatus == 400));
        Assert.Equal(8, rotated.Records.Count(x => x.Response!.HttpStatus == 200));
        Assert.Contains(rotated.Records, x => x.Id == error.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationOnlyRequiresDeletionOfBodyFreeSuccessRotationCandidate(bool protectedSuccess)
    {
        using var f = new Fixture();
        var persistence = PoolStore(f.Root, f.Clock);
        // An older record in the other pool must not hide a blocked rotation target.
        var other = PoolRecord(f.Clock.GetUtcNow(), !protectedSuccess);
        Assert.True(persistence.Save(other));
        PrivateProviderErrorRecord? oldest = null;
        for (int i = 0; i < 8; i++)
        {
            f.Clock.Now += TimeSpan.FromSeconds(1);
            var record = PoolRecord(f.Clock.GetUtcNow(), protectedSuccess);
            oldest ??= record; Assert.True(persistence.Save(record));
        }
        string path = Assert.Single(Directory.GetFiles(f.Root, "*" + oldest!.Id.ToString("N") + ".pdiag"));
        try
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            Assert.Equal(!protectedSuccess, persistence.PrepareProtectedCapture());
            Assert.Equal(9, persistence.Read().Count);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.True(persistence.PrepareProtectedCapture());
    }

    private static IPrivateProviderErrorPersistence PoolStore(string root, TimeProvider clock) =>
        (IPrivateProviderErrorPersistence)Activator.CreateInstance(typeof(GooglePrivateErrorDiagnostics).Assembly.GetType(
            "CloudScribe.Infrastructure.Generation.FilePrivateProviderErrorStore", throwOnError: true)!, root, clock)!;

    private static PrivateProviderErrorRecord PoolRecord(DateTimeOffset now, bool success) =>
        new(Guid.NewGuid(), now, "Bounded response summary", false, new string('a', 64), Guid.NewGuid().ToString("N"), true,
            new(1, success ? 200 : 400, 100, true, "Complete", success ? "SuccessBodyExcluded" : "ValidError",
                success ? "" : "Unique retained explanation", false, true, false,
                success ? GoogleErrorStatus.NotProvided : GoogleErrorStatus.InvalidArgument));
}

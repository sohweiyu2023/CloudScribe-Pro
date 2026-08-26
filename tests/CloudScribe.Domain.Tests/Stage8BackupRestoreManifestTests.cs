using System.Security.Cryptography;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8BackupRestoreManifestTests
{
    [Fact]
    public async Task RestoreVerificationRequiresExactLengthAndDigest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = "backup-payload"u8.ToArray();
            var path = Path.Combine(root, "db.bin");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var entry = new BackupFileEntry("db.bin", bytes.Length, hash).Validate();

            await BackupRestoreManifest.VerifyFileAsync(root, entry, cancellationToken);

            await File.AppendAllTextAsync(path, "tamper", cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => BackupRestoreManifest.VerifyFileAsync(root, entry, cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnsafeAndCollidingRestorePathsAreRejected()
    {
        var hash = new string('a', 64);
        Assert.Throws<InvalidOperationException>(() => new BackupFileEntry("../escape", 1, hash).Validate());

        var manifest = new BackupRestoreManifest(1, DateTimeOffset.UtcNow,
        [
            new BackupFileEntry("Data/db.bin", 1, hash),
            new BackupFileEntry("data/DB.bin", 1, hash),
        ]);
        Assert.Throws<InvalidOperationException>(manifest.Validate);
    }
}

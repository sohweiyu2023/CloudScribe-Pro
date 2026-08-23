using System.Security.Cryptography;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8AtomicVerifiedRestoreExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CopiesVerifiesAndCommitsBoundPlan()
    {
        var root = NewTempDirectory();
        try
        {
            var backup = Path.Combine(root, "backup");
            var restore = Path.Combine(root, "restore");
            Directory.CreateDirectory(Path.Combine(backup, "data"));
            Directory.CreateDirectory(restore);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(Path.Combine(backup, "data", "db.bin"), bytes);
            var manifest = Manifest("data/db.bin", bytes);
            var plan = RestoreExecutionPlan.Create(restore, manifest, 1024, 10);
            var journal = RestoreTransactionJournal.Start(plan, DateTimeOffset.UtcNow.AddSeconds(-1));
            var executor = new AtomicVerifiedRestoreExecutor();

            var completed = await executor.ExecuteAsync(backup, plan, journal);

            Assert.Equal(RestoreTransactionState.Committed, completed.State);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(restore, "data", "db.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TamperedBackupFailsClosedWithoutPublishingDestination()
    {
        var root = NewTempDirectory();
        try
        {
            var backup = Path.Combine(root, "backup");
            var restore = Path.Combine(root, "restore");
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(restore);
            var approved = new byte[] { 1, 2, 3 };
            var source = Path.Combine(backup, "db.bin");
            await File.WriteAllBytesAsync(source, approved);
            var manifest = Manifest("db.bin", approved);
            var plan = RestoreExecutionPlan.Create(restore, manifest, 1024, 10);
            var journal = RestoreTransactionJournal.Start(plan, DateTimeOffset.UtcNow.AddSeconds(-1));
            await File.WriteAllBytesAsync(source, new byte[] { 9, 9, 9 });
            var executor = new AtomicVerifiedRestoreExecutor();

            var error = await Assert.ThrowsAsync<RestoreExecutionFailureException>(
                () => executor.ExecuteAsync(backup, plan, journal));

            Assert.Equal(RestoreTransactionState.RollbackRequired, error.RollbackJournal.State);
            Assert.False(File.Exists(Path.Combine(restore, "db.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PreexistingDestinationIsNeverOverwrittenOrDeleted()
    {
        var root = NewTempDirectory();
        try
        {
            var backup = Path.Combine(root, "backup");
            var restore = Path.Combine(root, "restore");
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(restore);
            var approved = new byte[] { 4, 5, 6 };
            await File.WriteAllBytesAsync(Path.Combine(backup, "db.bin"), approved);
            var destination = Path.Combine(restore, "db.bin");
            var existing = new byte[] { 7, 7, 7 };
            await File.WriteAllBytesAsync(destination, existing);
            var manifest = Manifest("db.bin", approved);
            var plan = RestoreExecutionPlan.Create(restore, manifest, 1024, 10);
            var journal = RestoreTransactionJournal.Start(plan, DateTimeOffset.UtcNow.AddSeconds(-1));
            var executor = new AtomicVerifiedRestoreExecutor();

            await Assert.ThrowsAsync<RestoreExecutionFailureException>(
                () => executor.ExecuteAsync(backup, plan, journal));

            Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BackupRestoreManifest Manifest(string relativePath, byte[] bytes) => new(
        1,
        DateTimeOffset.UtcNow,
        new[]
        {
            new BackupFileEntry(
                relativePath,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
        });

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

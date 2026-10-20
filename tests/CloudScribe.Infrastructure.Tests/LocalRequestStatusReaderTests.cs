using System.Security.Cryptography;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class LocalRequestStatusReaderTests
{
    [Fact]
    public async Task ReportsUnresolvedRowsWithoutModifyingQueueOrExposingRawIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-status-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root }));
        paths.EnsureDatabaseDirectory();
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ConnectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE google_generation_queue_states (IdempotencyKey TEXT,UnresolvedSubmission INTEGER); INSERT INTO google_generation_queue_states VALUES ('private-fixture-id',1),('clean-fixture-id',0);";
                _ = await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
            LocalRequestStatus status = await new LocalRequestStatusReader(paths).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, status.Total); Assert.Equal(1, status.Unresolved);
            Assert.Contains(status.Rows, r => r.Contains("may have been charged", StringComparison.Ordinal));
            Assert.DoesNotContain(status.Rows, r => r.Contains("private-fixture-id", StringComparison.Ordinal));
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(paths.DatabasePath, TestContext.Current.CancellationToken)));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}

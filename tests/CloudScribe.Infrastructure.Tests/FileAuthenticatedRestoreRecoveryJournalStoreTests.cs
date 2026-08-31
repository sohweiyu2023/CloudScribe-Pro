using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class FileAuthenticatedRestoreRecoveryJournalStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsAuthenticatedInterruptedJournal()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "restore-recovery.journal");
            byte[] key = CreateAuthenticationKey();
            var journal = new RestoreTransactionJournal(
                Guid.NewGuid(),
                new string('a', 64),
                RestoreTransactionState.Copying,
                ["audio/part-001.wav"],
                new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero));

            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(path, key);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            Assert.Null(await store.LoadAuthenticatedAsync(cancellationToken));

            await store.SaveAsync(journal, cancellationToken);
            RestoreTransactionJournal? restored = await store.LoadAuthenticatedAsync(cancellationToken);

            Assert.NotNull(restored);
            Assert.Equal(journal.TransactionId, restored.TransactionId);
            Assert.Equal(journal.PlanSha256, restored.PlanSha256);
            Assert.Equal(journal.State, restored.State);
            Assert.Equal(journal.CompletedRelativePaths, restored.CompletedRelativePaths);
            Assert.Equal(journal.UpdatedAtUtc, restored.UpdatedAtUtc);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task LoadRejectsJournalWhoseAuthenticationTagWasChanged()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "restore-recovery.journal");
            byte[] key = CreateAuthenticationKey();
            var journal = new RestoreTransactionJournal(
                Guid.NewGuid(),
                new string('b', 64),
                RestoreTransactionState.RollbackRequired,
                ["audio/part-001.wav"],
                new DateTimeOffset(2026, 8, 30, 13, 1, 0, TimeSpan.Zero));

            using var store = new FileAuthenticatedRestoreRecoveryJournalStore(path, key);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await store.SaveAsync(journal, cancellationToken);

            string document = await File.ReadAllTextAsync(path, cancellationToken);
            string[] lines = document.Split('\n', StringSplitOptions.None);
            Assert.True(lines.Length >= 3);
            char replacement = lines[2][^1] == '0' ? '1' : '0';
            lines[2] = lines[2][..^1] + replacement;
            await File.WriteAllTextAsync(path, string.Join('\n', lines), cancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAuthenticatedAsync(cancellationToken));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static byte[] CreateAuthenticationKey()
    {
        var key = new byte[32];
        for (var index = 0; index < key.Length; index++)
        {
            key[index] = checked((byte)(index + 1));
        }

        return key;
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-stage8-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

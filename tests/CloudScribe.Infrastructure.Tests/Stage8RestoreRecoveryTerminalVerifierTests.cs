using System.Security.Cryptography;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8RestoreRecoveryTerminalVerifierTests
{
    [Fact]
    public async Task VerifyAsync_CommittedJournalRequiresExactDestinationBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        byte[] payload = [1, 2, 3, 4];
        string relative = "library/item.bin";
        string destination = Path.Combine(temp.Path, "library", "item.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, payload, cancellationToken);

        var plan = CreatePlan(temp.Path, relative, destination, payload);
        var now = DateTimeOffset.UtcNow;
        var journal = RestoreTransactionJournal.Start(plan, now)
            .BeginCopy(plan, now)
            .MarkCopied(plan, relative, now)
            .BeginVerification(plan, now)
            .Commit(plan, now);
        var verifier = new RestoreRecoveryTerminalVerifier();

        Assert.True(await verifier.VerifyAsync("verified-apply-resumed", plan, journal, cancellationToken));

        await File.WriteAllBytesAsync(destination, [9, 9, 9, 9], cancellationToken);
        Assert.False(await verifier.VerifyAsync("verified-apply-resumed", plan, journal, cancellationToken));
    }

    [Fact]
    public async Task VerifyAsync_RolledBackJournalRequiresTransactionOutputsAbsent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        byte[] payload = [5, 6, 7];
        string relative = "restored.bin";
        string destination = Path.Combine(temp.Path, relative);
        var plan = CreatePlan(temp.Path, relative, destination, payload);
        var now = DateTimeOffset.UtcNow;
        var journal = RestoreTransactionJournal.Start(plan, now)
            .BeginCopy(plan, now)
            .MarkCopied(plan, relative, now)
            .RequireRollback(plan, now)
            .CompleteRollback(plan, now);
        var verifier = new RestoreRecoveryTerminalVerifier();

        Assert.True(await verifier.VerifyAsync("rollback-completed", plan, journal, cancellationToken));

        await File.WriteAllBytesAsync(destination, payload, cancellationToken);
        Assert.False(await verifier.VerifyAsync("rollback-completed", plan, journal, cancellationToken));
    }

    private static RestoreExecutionPlan CreatePlan(
        string restoreRoot,
        string relativePath,
        string destinationPath,
        byte[] payload)
    {
        string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var step = new RestoreExecutionStep(relativePath, destinationPath, payload.LongLength, sha);
        return new RestoreExecutionPlan(Path.GetFullPath(restoreRoot), [step], payload.LongLength);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudscribe-stage8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

using System.Security.Cryptography;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8RestoreRecoveryTerminalVerifierTests
{
    [Fact]
    public async Task VerifyAsyncCommittedJournalRequiresExactDestinationBytes()
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

        Assert.True(await RestoreRecoveryTerminalVerifier.VerifyAsync("verified-apply-resumed", plan, journal, cancellationToken));

        await File.WriteAllBytesAsync(destination, [9, 9, 9, 9], cancellationToken);
        Assert.False(await RestoreRecoveryTerminalVerifier.VerifyAsync("verified-apply-resumed", plan, journal, cancellationToken));
    }

    [Fact]
    public async Task VerifyAsyncCommittedJournalRejectsMissingDestination()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        byte[] payload = [10, 11, 12];
        string relative = "library/missing.bin";
        string destination = Path.Combine(temp.Path, "library", "missing.bin");
        var plan = CreatePlan(temp.Path, relative, destination, payload);
        var now = DateTimeOffset.UtcNow;
        var journal = RestoreTransactionJournal.Start(plan, now)
            .BeginCopy(plan, now)
            .MarkCopied(plan, relative, now)
            .BeginVerification(plan, now)
            .Commit(plan, now);

        Assert.False(await RestoreRecoveryTerminalVerifier.VerifyAsync("verified-apply-resumed", plan, journal, cancellationToken));
    }

    [Fact]
    public async Task VerifyAsyncRejectsReparsePointInsideRestoreRoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        byte[] payload = [21, 22, 23, 24];
        string linkedDirectory = Path.Combine(temp.Path, "library");

        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, external.Path);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        string relative = "library/item.bin";
        string destination = Path.Combine(temp.Path, relative);
        await File.WriteAllBytesAsync(Path.Combine(external.Path, "item.bin"), payload, cancellationToken);

        var plan = CreatePlan(temp.Path, relative, destination, payload);
        var now = DateTimeOffset.UtcNow;
        var journal = RestoreTransactionJournal.Start(plan, now)
            .BeginCopy(plan, now)
            .MarkCopied(plan, relative, now)
            .BeginVerification(plan, now)
            .Commit(plan, now);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RestoreRecoveryTerminalVerifier.VerifyAsync("verified-apply-resumed", plan, journal, cancellationToken));
        Assert.Contains("reparse-point destination path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsyncRolledBackJournalRequiresTransactionOutputsAbsent()
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

        Assert.True(await RestoreRecoveryTerminalVerifier.VerifyAsync("rollback-completed", plan, journal, cancellationToken));

        await File.WriteAllBytesAsync(destination, payload, cancellationToken);
        Assert.False(await RestoreRecoveryTerminalVerifier.VerifyAsync("rollback-completed", plan, journal, cancellationToken));
    }

    [Fact]
    public async Task VerifyAsyncRejectsUnsupportedTerminalOutcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        byte[] payload = [13, 14];
        string relative = "unsupported.bin";
        string destination = Path.Combine(temp.Path, relative);
        var plan = CreatePlan(temp.Path, relative, destination, payload);
        var journal = RestoreTransactionJournal.Start(plan, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RestoreRecoveryTerminalVerifier.VerifyAsync("fabricated-success", plan, journal, cancellationToken));
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

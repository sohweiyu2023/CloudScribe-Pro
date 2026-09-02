using System.Security.Cryptography;
using CloudScribe.Domain.Safety;
using CloudScribe.Infrastructure.Safety;

namespace CloudScribe.Infrastructure.Tests;

public sealed class RestoreRecoveryTerminalVerifierTests
{
    [Fact]
    public async Task VerifyCommittedRequiresExactFilesystemContent()
    {
        string root = CreateTemporaryRoot();
        try
        {
            byte[] payload = [1, 2, 3, 4, 5];
            RestoreExecutionPlan plan = CreatePlan(root, payload);
            string destination = plan.Steps[0].DestinationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, payload, TestContext.Current.CancellationToken);
            RestoreTransactionJournal journal = CreateJournal(plan, RestoreTransactionState.Committed, [plan.Steps[0].RelativePath]);

            bool verified = await RestoreRecoveryTerminalVerifier.VerifyAsync(
                "verified-apply-resumed",
                plan,
                journal,
                TestContext.Current.CancellationToken);

            Assert.True(verified);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task VerifyCommittedFailsClosedWhenDestinationContentChanged()
    {
        string root = CreateTemporaryRoot();
        try
        {
            byte[] payload = [1, 2, 3, 4, 5];
            RestoreExecutionPlan plan = CreatePlan(root, payload);
            string destination = plan.Steps[0].DestinationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, [9, 9, 9, 9, 9], TestContext.Current.CancellationToken);
            RestoreTransactionJournal journal = CreateJournal(plan, RestoreTransactionState.Committed, [plan.Steps[0].RelativePath]);

            bool verified = await RestoreRecoveryTerminalVerifier.VerifyAsync(
                "verified-apply-resumed",
                plan,
                journal,
                TestContext.Current.CancellationToken);

            Assert.False(verified);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task VerifyRollbackFailsClosedWhenDestinationStillExists()
    {
        string root = CreateTemporaryRoot();
        try
        {
            byte[] payload = [1, 2, 3];
            RestoreExecutionPlan plan = CreatePlan(root, payload);
            string destination = plan.Steps[0].DestinationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, payload, TestContext.Current.CancellationToken);
            RestoreTransactionJournal journal = CreateJournal(plan, RestoreTransactionState.RolledBack, Array.Empty<string>());

            bool verified = await RestoreRecoveryTerminalVerifier.VerifyAsync(
                "rollback-completed",
                plan,
                journal,
                TestContext.Current.CancellationToken);

            Assert.False(verified);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static RestoreExecutionPlan CreatePlan(string restoreRoot, byte[] payload)
    {
        string fullRoot = Path.GetFullPath(restoreRoot);
        const string relativePath = "audio/part-001.wav";
        string destination = Path.Combine(fullRoot, "audio", "part-001.wav");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var step = new RestoreExecutionStep(relativePath, destination, payload.LongLength, sha256);
        return new RestoreExecutionPlan(fullRoot, [step], payload.LongLength);
    }

    private static RestoreTransactionJournal CreateJournal(
        RestoreExecutionPlan plan,
        RestoreTransactionState state,
        IReadOnlyList<string> completedRelativePaths)
    {
        return new RestoreTransactionJournal(
            Guid.NewGuid(),
            RestoreTransactionJournal.ComputePlanSha256(plan),
            state,
            completedRelativePaths,
            new DateTimeOffset(2026, 9, 2, 8, 30, 0, TimeSpan.Zero));
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-stage8-terminal-verifier-tests",
            Guid.NewGuid().ToString("N"));
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

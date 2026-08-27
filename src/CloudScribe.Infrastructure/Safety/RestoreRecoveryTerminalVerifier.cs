using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreRecoveryTerminalVerifier
{
    public async Task<bool> VerifyAsync(
        string outcome,
        RestoreExecutionPlan plan,
        RestoreTransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();

        journal.EnsurePlan(plan);
        RequirePhysicalRestoreRoot(plan.RestoreRoot);

        switch (outcome)
        {
            case "rollback-completed":
            case "no-op-terminal-rolled-back":
                return VerifyRolledBack(plan, journal, cancellationToken);

            case "verified-apply-resumed":
                return await VerifyCommittedAsync(plan, journal, cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unsupported restore recovery outcome for terminal verification: {outcome}");
        }
    }

    private static bool VerifyRolledBack(
        RestoreExecutionPlan plan,
        RestoreTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.State != RestoreTransactionState.RolledBack)
            return false;
        if (journal.CompletedRelativePaths.Count != 0)
            return false;

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = RequireBoundDestination(plan.RestoreRoot, step);
            if (File.Exists(destination) || Directory.Exists(destination))
                return false;
        }

        return true;
    }

    private static async Task<bool> VerifyCommittedAsync(
        RestoreExecutionPlan plan,
        RestoreTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.State != RestoreTransactionState.Committed)
            return false;
        if (journal.CompletedRelativePaths.Count != plan.Steps.Count)
            return false;

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!journal.CompletedRelativePaths.Contains(step.RelativePath, StringComparer.Ordinal))
                return false;

            _ = RequireBoundDestination(plan.RestoreRoot, step);
            try
            {
                await BackupRestoreManifest.VerifyFileAsync(
                    plan.RestoreRoot,
                    new BackupFileEntry(step.RelativePath, step.Length, step.Sha256),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    private static string RequireBoundDestination(string restoreRoot, RestoreExecutionStep step)
    {
        var root = Path.GetFullPath(restoreRoot);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var expected = Path.GetFullPath(Path.Combine(root, step.RelativePath));
        if (!expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore recovery verification path escapes the restore root.");
        if (!string.Equals(expected, Path.GetFullPath(step.DestinationPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore recovery verification destination differs from the bound plan.");
        return expected;
    }

    private static void RequirePhysicalRestoreRoot(string restoreRoot)
    {
        var current = new DirectoryInfo(Path.GetFullPath(restoreRoot));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Restore recovery verification may not traverse a reparse-point directory: {current.FullName}");
            current = current.Parent;
        }
    }
}

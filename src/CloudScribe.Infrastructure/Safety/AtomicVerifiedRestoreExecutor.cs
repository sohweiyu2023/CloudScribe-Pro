using System.Security.Cryptography;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class AtomicVerifiedRestoreExecutor
{
    private const int BufferSize = 81920;
    private readonly TimeProvider _timeProvider;

    public AtomicVerifiedRestoreExecutor(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RestoreTransactionJournal> ExecuteAsync(
        string backupRoot,
        RestoreExecutionPlan plan,
        RestoreTransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(journal);
        journal.EnsurePlan(plan);

        var sourceRoot = Path.GetFullPath(backupRoot);
        if (!Path.IsPathFullyQualified(sourceRoot))
            throw new InvalidOperationException("Backup source root must resolve to an absolute path.");
        RequirePhysicalDirectory(sourceRoot, "Backup source root");
        EnsurePhysicalDirectory(plan.RestoreRoot, "Restore root");

        var createdThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (journal.State == RestoreTransactionState.Pending)
                journal = journal.BeginCopy(plan, NowAfter(journal.UpdatedAtUtc));

            if (journal.State == RestoreTransactionState.Copying)
            {
                foreach (var step in plan.Steps.OrderBy(static x => x.RelativePath, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (journal.CompletedRelativePaths.Contains(step.RelativePath, StringComparer.Ordinal))
                        continue;

                    await CopyVerifiedAsync(sourceRoot, plan.RestoreRoot, step, cancellationToken).ConfigureAwait(false);
                    createdThisRun.Add(step.DestinationPath);
                    journal = journal.MarkCopied(plan, step.RelativePath, NowAfter(journal.UpdatedAtUtc));
                }

                journal = journal.BeginVerification(plan, NowAfter(journal.UpdatedAtUtc));
            }

            if (journal.State == RestoreTransactionState.Verifying)
            {
                foreach (var step in plan.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await VerifyDestinationAsync(plan.RestoreRoot, step, cancellationToken).ConfigureAwait(false);
                }

                journal = journal.Commit(plan, NowAfter(journal.UpdatedAtUtc));
            }

            if (journal.State == RestoreTransactionState.Committed)
                return journal;

            throw new InvalidOperationException($"Restore transaction cannot execute from state {journal.State}.");
        }
        catch (Exception ex) when (ex is not RestoreExecutionFailureException)
        {
            var rollback = journal.State is RestoreTransactionState.Committed or RestoreTransactionState.RollbackRequired
                ? journal
                : journal.RequireRollback(plan, NowAfter(journal.UpdatedAtUtc));

            await DeleteTransactionOutputsAsync(plan, rollback.CompletedRelativePaths, createdThisRun).ConfigureAwait(false);
            throw new RestoreExecutionFailureException("Restore execution failed and requires rollback/recovery.", rollback, ex);
        }
    }

    public Task RollbackAsync(
        RestoreExecutionPlan plan,
        RestoreTransactionJournal rollbackJournal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rollbackJournal);
        rollbackJournal.EnsurePlan(plan);
        if (rollbackJournal.State != RestoreTransactionState.RollbackRequired)
            throw new InvalidOperationException("Rollback execution requires a RollbackRequired journal state.");
        cancellationToken.ThrowIfCancellationRequested();
        return DeleteTransactionOutputsAsync(plan, rollbackJournal.CompletedRelativePaths, Array.Empty<string>());
    }

    private static async Task CopyVerifiedAsync(
        string sourceRoot,
        string restoreRoot,
        RestoreExecutionStep step,
        CancellationToken cancellationToken)
    {
        var source = ValidateSource(sourceRoot, step);
        var destination = PrepareDestination(restoreRoot, step);
        var temporary = destination + ".restore-tmp-" + Guid.NewGuid().ToString("N");
        var destinationPublished = false;
        try
        {
            await CopyAndVerifySourceAsync(source, temporary, step, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: false);
            destinationPublished = true;
            await VerifyDestinationAsync(restoreRoot, step, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (destinationPublished && File.Exists(destination))
                File.Delete(destination);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ValidateSource(string sourceRoot, RestoreExecutionStep step)
    {
        var source = ResolveContained(sourceRoot, step.RelativePath, "Backup source path escapes the backup root.");
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists) throw new FileNotFoundException("Backup source file is missing.", source);
        if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Backup source files must not be symbolic links or reparse points.");
        if (sourceInfo.Length != step.Length)
            throw new InvalidDataException($"Backup source length mismatch for {step.RelativePath}.");
        return source;
    }

    private static string PrepareDestination(string restoreRoot, RestoreExecutionStep step)
    {
        var destination = Path.GetFullPath(step.DestinationPath);
        var expectedDestination = ResolveContained(restoreRoot, step.RelativePath, "Restore destination escapes the restore root.");
        if (!string.Equals(destination, expectedDestination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore plan destination identity changed after planning.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"Restore destination already exists: {step.RelativePath}");

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Restore destination has no parent directory.");
        Directory.CreateDirectory(parent);
        EnsureNoReparseDirectoryChain(restoreRoot, parent);
        return destination;
    }

    private static async Task CopyAndVerifySourceAsync(
        string source,
        string temporary,
        RestoreExecutionStep step,
        CancellationToken cancellationToken)
    {
        var inputStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var input = inputStream.ConfigureAwait(false);
        var outputStream = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var output = outputStream.ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[BufferSize];
        long written = 0;
        while (true)
        {
            var read = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            written = checked(written + read);
            if (written > step.Length)
                throw new InvalidDataException($"Backup source grew beyond the planned length for {step.RelativePath}.");
            hash.AppendData(buffer, 0, read);
            await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (written != step.Length)
            throw new InvalidDataException($"Backup source length changed while restoring {step.RelativePath}.");
        var observed = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(observed, Convert.FromHexString(step.Sha256)))
            throw new InvalidDataException($"Backup source digest mismatch for {step.RelativePath}.");
    }

    private static Task VerifyDestinationAsync(
        string restoreRoot,
        RestoreExecutionStep step,
        CancellationToken cancellationToken) =>
        BackupRestoreManifest.VerifyFileAsync(
            restoreRoot,
            new BackupFileEntry(step.RelativePath, step.Length, step.Sha256),
            cancellationToken);

    private static string ResolveContained(string root, string relativePath, string error)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(error);
        return fullPath;
    }

    private static void RequirePhysicalDirectory(string directory, string description)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"{description} does not exist: {info.FullName}");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{description} must not be a symbolic link or reparse point.");
    }

    private static void EnsurePhysicalDirectory(string directory, string description)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) Directory.CreateDirectory(info.FullName);
        info.Refresh();
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{description} must not be a symbolic link or reparse point.");
    }

    private static void EnsureNoReparseDirectoryChain(string root, string destinationParent)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = new DirectoryInfo(Path.GetFullPath(destinationParent));
        while (true)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Restore destination directory chain contains a symbolic link or reparse point.");
            if (string.Equals(current.FullName, fullRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent ?? throw new InvalidOperationException("Restore destination parent escaped the restore root.");
        }
    }

    private static Task DeleteTransactionOutputsAsync(
        RestoreExecutionPlan plan,
        IEnumerable<string> completedRelativePaths,
        IEnumerable<string> createdThisRun)
    {
        var paths = new HashSet<string>(createdThisRun, StringComparer.OrdinalIgnoreCase);
        foreach (var relative in completedRelativePaths)
        {
            var step = plan.Steps.SingleOrDefault(x => string.Equals(x.RelativePath, relative, StringComparison.Ordinal))
                ?? throw new InvalidDataException("Rollback journal references a path absent from the bound restore plan.");
            paths.Add(step.DestinationPath);
        }

        foreach (var path in paths.OrderByDescending(static x => x.Length))
        {
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private DateTimeOffset NowAfter(DateTimeOffset previous)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < previous ? previous : now;
    }
}

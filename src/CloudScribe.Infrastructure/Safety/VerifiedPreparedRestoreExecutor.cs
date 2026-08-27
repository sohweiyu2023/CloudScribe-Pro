using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class VerifiedPreparedRestoreExecutor
{
    private readonly AtomicVerifiedRestoreExecutor _executor;
    private readonly TimeProvider _timeProvider;

    public VerifiedPreparedRestoreExecutor(
        AtomicVerifiedRestoreExecutor executor,
        TimeProvider? timeProvider = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RestoreTransactionJournal> ExecuteAsync(
        string stagingRoot,
        string restoreRoot,
        BackupRestoreManifest manifest,
        IReadOnlyList<RestoreManifestFileBinding> verifiedBindings,
        RestoreTransactionJournal journal,
        long maximumTotalBytes,
        int maximumFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verifiedBindings);
        ArgumentNullException.ThrowIfNull(journal);

        var canonicalStaging = Path.GetFullPath(stagingRoot);
        var canonicalRestore = Path.GetFullPath(restoreRoot);
        RequireDisjointRoots(canonicalStaging, canonicalRestore);
        RequireNonReparsePathChain(canonicalStaging, "restore staging");
        RequireNonReparsePathChain(canonicalRestore, "restore destination");

        if (journal.State == RestoreTransactionState.RollbackRequired)
            throw new InvalidOperationException("A rollback-required restore transaction must be rolled back before any new execution attempt.");
        if (journal.State == RestoreTransactionState.RolledBack)
            throw new InvalidOperationException("A rolled-back restore transaction is terminal; start a new verified restore transaction instead of reusing its journal.");

        var plan = RestoreExecutionPlanPolicy.PrepareVerified(
            canonicalStaging,
            canonicalRestore,
            manifest,
            verifiedBindings,
            maximumTotalBytes,
            maximumFiles);

        journal.EnsurePlan(plan);
        return _executor.ExecuteAsync(canonicalStaging, plan, journal, cancellationToken);
    }

    public async Task<RestoreTransactionJournal> RollbackAsync(
        string stagingRoot,
        string restoreRoot,
        BackupRestoreManifest manifest,
        IReadOnlyList<RestoreManifestFileBinding> verifiedBindings,
        RestoreTransactionJournal rollbackJournal,
        long maximumTotalBytes,
        int maximumFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verifiedBindings);
        ArgumentNullException.ThrowIfNull(rollbackJournal);
        if (rollbackJournal.State != RestoreTransactionState.RollbackRequired)
            throw new InvalidOperationException("Verified restore rollback requires a RollbackRequired journal.");

        var canonicalStaging = Path.GetFullPath(stagingRoot);
        var canonicalRestore = Path.GetFullPath(restoreRoot);
        RequireDisjointRoots(canonicalStaging, canonicalRestore);
        RequireNonReparsePathChain(canonicalStaging, "restore staging");
        RequireNonReparsePathChain(canonicalRestore, "restore destination");

        var plan = RestoreExecutionPlanPolicy.PrepareVerified(
            canonicalStaging,
            canonicalRestore,
            manifest,
            verifiedBindings,
            maximumTotalBytes,
            maximumFiles);
        rollbackJournal.EnsurePlan(plan);

        await _executor.RollbackAsync(plan, rollbackJournal, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (now < rollbackJournal.UpdatedAtUtc) now = rollbackJournal.UpdatedAtUtc;
        return rollbackJournal.CompleteRollback(plan, now);
    }

    private static void RequireDisjointRoots(string stagingRoot, string restoreRoot)
    {
        var stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar) ? stagingRoot : stagingRoot + Path.DirectorySeparatorChar;
        var restorePrefix = restoreRoot.EndsWith(Path.DirectorySeparatorChar) ? restoreRoot : restoreRoot + Path.DirectorySeparatorChar;

        if (string.Equals(stagingRoot, restoreRoot, StringComparison.OrdinalIgnoreCase) ||
            stagingRoot.StartsWith(restorePrefix, StringComparison.OrdinalIgnoreCase) ||
            restoreRoot.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore staging and destination roots must be disjoint physical namespaces.");
    }

    private static void RequireNonReparsePathChain(string path, string label)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"The {label} path may not traverse a symbolic link or reparse-point directory: {current.FullName}");
            current = current.Parent;
        }
    }
}

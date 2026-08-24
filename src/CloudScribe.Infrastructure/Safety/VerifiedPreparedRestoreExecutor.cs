using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class VerifiedPreparedRestoreExecutor
{
    private readonly AtomicVerifiedRestoreExecutor _executor;

    public VerifiedPreparedRestoreExecutor(AtomicVerifiedRestoreExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
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

        if (journal.State == RestoreTransactionState.RollbackRequired)
            throw new InvalidOperationException("A rollback-required restore transaction must be rolled back before any new execution attempt.");

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

    private static void RequireDisjointRoots(string stagingRoot, string restoreRoot)
    {
        var stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar) ? stagingRoot : stagingRoot + Path.DirectorySeparatorChar;
        var restorePrefix = restoreRoot.EndsWith(Path.DirectorySeparatorChar) ? restoreRoot : restoreRoot + Path.DirectorySeparatorChar;

        if (string.Equals(stagingRoot, restoreRoot, StringComparison.OrdinalIgnoreCase) ||
            stagingRoot.StartsWith(restorePrefix, StringComparison.OrdinalIgnoreCase) ||
            restoreRoot.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore staging and destination roots must be disjoint physical namespaces.");
    }
}

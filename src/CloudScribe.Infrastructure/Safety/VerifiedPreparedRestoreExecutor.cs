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

        var plan = RestoreExecutionPlanPolicy.PrepareVerified(
            stagingRoot,
            restoreRoot,
            manifest,
            verifiedBindings,
            maximumTotalBytes,
            maximumFiles);

        journal.EnsurePlan(plan);
        return _executor.ExecuteAsync(stagingRoot, plan, journal, cancellationToken);
    }
}

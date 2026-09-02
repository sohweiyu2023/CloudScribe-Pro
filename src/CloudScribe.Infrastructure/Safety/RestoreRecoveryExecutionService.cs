using CloudScribe.Application.Safety;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreRecoveryExecutionService
{
    private readonly RestoreRecoveryStateResolver _stateResolver;
    private readonly FileAuthenticatedRestoreRecoveryJournalStore _journalStore;
    private readonly AtomicVerifiedRestoreExecutor _restoreExecutor;
    private readonly string _backupRoot;
    private readonly TimeProvider _timeProvider;

    public RestoreRecoveryExecutionService(
        RestoreRecoveryStateResolver stateResolver,
        FileAuthenticatedRestoreRecoveryJournalStore journalStore,
        AtomicVerifiedRestoreExecutor restoreExecutor,
        string backupRoot,
        TimeProvider timeProvider)
    {
        _stateResolver = stateResolver ?? throw new ArgumentNullException(nameof(stateResolver));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _restoreExecutor = restoreExecutor ?? throw new ArgumentNullException(nameof(restoreExecutor));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        if (!Path.IsPathFullyQualified(backupRoot))
            throw new InvalidOperationException("Restore recovery backup root must be explicitly fully qualified.");
        _backupRoot = Path.GetFullPath(backupRoot);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<string?> RecoverPersistedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreTransactionJournal? journal = await _journalStore
            .LoadAuthenticatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (journal is null)
            return null;

        RestoreExecutionPlan plan = journal.RequirePersistedPlan();
        return await RecoverAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> RecoverAsync(
        RestoreExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RestoreRecoveryContext? context = await _stateResolver
            .ResolveAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
            return null;

        RestoreTransactionJournal journal = context.Journal;
        using var coordinator = new RestoreRecoveryCoordinator(
            rollbackAsync: async ct =>
            {
                await AtomicVerifiedRestoreExecutor.RollbackAsync(plan, journal, ct).ConfigureAwait(false);
                journal = journal.CompleteRollback(plan, NowAfter(journal.UpdatedAtUtc));
                await _journalStore.SaveAsync(journal, ct).ConfigureAwait(false);
            },
            resumeVerifiedApplyAsync: async ct =>
            {
                try
                {
                    journal = await _restoreExecutor
                        .ExecuteAsync(_backupRoot, plan, journal, ct)
                        .ConfigureAwait(false);
                    await _journalStore.SaveAsync(journal, ct).ConfigureAwait(false);
                }
                catch (RestoreExecutionFailureException ex)
                {
                    journal = ex.RollbackJournal;
                    await _journalStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            });

        return await coordinator.RecoverVerifiedAsync(
            context.State,
            (outcome, ct) => RestoreRecoveryTerminalVerifier.VerifyAsync(
                outcome,
                plan,
                journal,
                ct),
            cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset NowAfter(DateTimeOffset previous)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < previous ? previous : now;
    }
}

namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreRecoveryExecutionComposition : IDisposable
{
    private readonly FileAuthenticatedRestoreRecoveryJournalStore _journalStore;
    private bool _disposed;

    internal RestoreRecoveryExecutionComposition(
        RestoreRecoveryExecutionService service,
        FileAuthenticatedRestoreRecoveryJournalStore journalStore)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
    }

    public RestoreRecoveryExecutionService Service { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _journalStore.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

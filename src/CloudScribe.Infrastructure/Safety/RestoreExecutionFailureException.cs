using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed class RestoreExecutionFailureException : IOException
{
    public RestoreExecutionFailureException(
        string message,
        RestoreTransactionJournal rollbackJournal,
        Exception innerException)
        : base(message, innerException)
    {
        RollbackJournal = rollbackJournal ?? throw new ArgumentNullException(nameof(rollbackJournal));
    }

    public RestoreTransactionJournal RollbackJournal { get; }
}

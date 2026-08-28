namespace CloudScribe.Domain.Safety;

public enum RestoreTransactionState
{
    Pending = 0,
    Copying = 1,
    Verifying = 2,
    Committed = 3,
    RollbackRequired = 4,
    RolledBack = 5,
}

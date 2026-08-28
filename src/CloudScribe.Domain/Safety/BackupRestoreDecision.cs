namespace CloudScribe.Domain.Safety;

public sealed record BackupRestoreDecision(bool MayRestore, string Reason);

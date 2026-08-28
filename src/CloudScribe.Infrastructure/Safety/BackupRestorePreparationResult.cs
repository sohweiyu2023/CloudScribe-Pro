using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed record BackupRestorePreparationResult(
    BackupRestoreDecision Decision,
    BackupRestoreStagingResult Staging);

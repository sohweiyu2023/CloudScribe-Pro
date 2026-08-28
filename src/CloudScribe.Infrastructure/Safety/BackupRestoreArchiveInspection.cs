namespace CloudScribe.Infrastructure.Safety;

public sealed record BackupRestoreArchiveInspection(
    bool ArchiveStructureValid,
    bool SecretsExcluded,
    bool NativePayloadsAllowed,
    bool PathTraversalSafe,
    int EntryCount);

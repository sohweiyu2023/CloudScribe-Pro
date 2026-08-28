namespace CloudScribe.Infrastructure.Safety;

public sealed record BackupRestoreStagingResult(
    string StagingDirectory,
    int FilesExtracted,
    long BytesExtracted);

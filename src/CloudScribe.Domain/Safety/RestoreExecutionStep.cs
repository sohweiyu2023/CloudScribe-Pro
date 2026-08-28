namespace CloudScribe.Domain.Safety;

public sealed record RestoreExecutionStep(
    string RelativePath,
    string DestinationPath,
    long Length,
    string Sha256);

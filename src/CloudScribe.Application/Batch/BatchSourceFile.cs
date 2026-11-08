namespace CloudScribe.Application.Batch;

/// <summary>A file the user selected, already decoded by the normal importer (or with the reason it could not be).</summary>
public sealed record BatchSourceFile(
    string FileName,
    string Sha256,
    long Bytes,
    string? Text,
    BatchExclusion Problem = BatchExclusion.None);

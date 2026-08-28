using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record AudioAssemblyExecutionArtifact(
    int PartNumber,
    string OutputPath,
    long LengthBytes,
    ReleaseAudioFormat Format);

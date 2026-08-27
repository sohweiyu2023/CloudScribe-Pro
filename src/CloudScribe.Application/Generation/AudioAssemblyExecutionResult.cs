namespace CloudScribe.Application.Generation;

public sealed record AudioAssemblyExecutionResult(
    IReadOnlyList<AudioAssemblyExecutionArtifact> Artifacts,
    IReadOnlyList<NativeMediaToolResult> NativeResults);

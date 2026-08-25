namespace CloudScribe.Domain.Generation;

public sealed record AudioAssemblyPart(
    int PartNumber,
    IReadOnlyList<AudioSegmentArtifact> Segments,
    TimeSpan MeasuredDuration);

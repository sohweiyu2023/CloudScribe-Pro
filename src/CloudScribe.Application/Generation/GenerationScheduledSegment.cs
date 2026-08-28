namespace CloudScribe.Application.Generation;

public sealed record GenerationScheduledSegment(
    Guid JobId,
    string SegmentId,
    int SegmentIndex,
    GenerationSegmentExecutionRequest Request);

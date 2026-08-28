using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationScheduledSegmentResult(
    GenerationSegmentProgress Progress,
    GenerationSegmentExecutionResult? ExecutionResult);

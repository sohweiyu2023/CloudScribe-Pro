using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationProofResult(
    Guid SegmentId,
    OutputQualityAssessment Quality,
    bool DurationWithinTolerance,
    string ProvenanceId)
{
    public bool IsReleaseSafe => Quality.Disposition == OutputQualityDisposition.Accepted;
}

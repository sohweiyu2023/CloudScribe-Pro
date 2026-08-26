namespace CloudScribe.Application.Generation;

public sealed record GenerationProofInput(
    Guid SegmentId,
    bool MediaValid,
    TimeSpan ExpectedDuration,
    TimeSpan ActualDuration,
    bool RequiredTimingMarksPresent,
    IReadOnlyList<string> ProviderDiagnostics,
    string ProvenanceId)
{
    public GenerationProofInput Validate()
    {
        ValidateFields(SegmentId, ExpectedDuration, ActualDuration, ProviderDiagnostics, ProvenanceId);
        return this;
    }

    private static void ValidateFields(
        Guid segmentId,
        TimeSpan expectedDuration,
        TimeSpan actualDuration,
        IReadOnlyList<string> providerDiagnostics,
        string provenanceId)
    {
        if (segmentId == Guid.Empty)
        {
            throw new ArgumentException("Segment id is required.", nameof(segmentId));
        }
        if (expectedDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDuration));
        }
        if (actualDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(actualDuration));
        }
        ArgumentNullException.ThrowIfNull(providerDiagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceId);
    }
}

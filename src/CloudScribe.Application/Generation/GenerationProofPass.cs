using CloudScribe.Domain.Generation;

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
        if (SegmentId == Guid.Empty)
        {
            throw new ArgumentException("Segment id is required.", nameof(SegmentId));
        }
        if (ExpectedDuration <= TimeSpan.Zero || ActualDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedDuration));
        }
        ArgumentNullException.ThrowIfNull(ProviderDiagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProvenanceId);
        return this;
    }
}

public sealed record GenerationProofResult(
    Guid SegmentId,
    OutputQualityAssessment Quality,
    bool DurationWithinTolerance,
    string ProvenanceId)
{
    public bool IsReleaseSafe => Quality.Disposition == OutputQualityDisposition.Accepted;
}

public sealed class GenerationProofPass
{
    private readonly double _maximumDurationDeviationRatio;

    public GenerationProofPass(double maximumDurationDeviationRatio = 0.20)
    {
        if (double.IsNaN(maximumDurationDeviationRatio) || double.IsInfinity(maximumDurationDeviationRatio) ||
            maximumDurationDeviationRatio is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDurationDeviationRatio));
        }
        _maximumDurationDeviationRatio = maximumDurationDeviationRatio;
    }

    public GenerationProofResult Evaluate(GenerationProofInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();

        var deviationTicks = Math.Abs(input.ActualDuration.Ticks - input.ExpectedDuration.Ticks);
        var allowedTicks = checked((long)Math.Ceiling(input.ExpectedDuration.Ticks * _maximumDurationDeviationRatio));
        var durationWithinTolerance = deviationTicks <= allowedTicks;
        var quality = OutputQualityAssessment.Evaluate(
            input.MediaValid,
            durationWithinTolerance,
            input.RequiredTimingMarksPresent,
            input.ProviderDiagnostics);

        return new GenerationProofResult(input.SegmentId, quality, durationWithinTolerance, input.ProvenanceId);
    }

    public IReadOnlyList<GenerationProofResult> EvaluateCollection(IEnumerable<GenerationProofInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var materialized = inputs.Select(Evaluate).ToArray();
        if (materialized.Select(static result => result.SegmentId).Distinct().Count() != materialized.Length)
        {
            throw new ArgumentException("Proof pass segment ids must be unique.", nameof(inputs));
        }
        return materialized;
    }

    public void EnsureReleaseSafe(IEnumerable<GenerationProofResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var quarantined = results.Where(static result => !result.IsReleaseSafe).Select(static result => result.SegmentId).ToArray();
        if (quarantined.Length != 0)
        {
            throw new InvalidOperationException($"Proof Pass quarantined {quarantined.Length} segment(s); release is blocked.");
        }
    }
}

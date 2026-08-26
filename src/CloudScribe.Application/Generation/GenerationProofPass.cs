using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

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

namespace CloudScribe.Domain.Generation;

public sealed record OutputQualityAssessment(
    OutputQualityDisposition Disposition,
    IReadOnlyList<string> DiagnosticCodes)
{
    public static OutputQualityAssessment Evaluate(
        bool mediaValid,
        bool durationWithinTolerance,
        bool containsRequiredTimingMarks,
        IEnumerable<string>? diagnostics = null)
    {
        var codes = diagnostics?.Where(static code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (!mediaValid)
        {
            codes.Add("quality.media.invalid");
        }
        if (!durationWithinTolerance)
        {
            codes.Add("quality.duration.out-of-range");
        }
        if (!containsRequiredTimingMarks)
        {
            codes.Add("quality.timing-marks.missing");
        }

        return new OutputQualityAssessment(
            codes.Count == 0 ? OutputQualityDisposition.Accepted : OutputQualityDisposition.Quarantined,
            codes);
    }
}

using System.Globalization;

namespace CloudScribe.App.Composition;

/// <summary>Honest standard-article progress without inferring historical timing or an ETA.</summary>
public static class WholeArticleRunPresentation
{
    public static double? CoveragePercentage(int verified, int total) => total <= 0 || verified < 0 || verified > total
        ? null : 100d * verified / total;

    public static string ElapsedText(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        string duration = ((long)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + elapsed.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
        return $"This-run elapsed: {duration} (includes provider waits and local processing). Earlier runs and pauses are not included; whole-job duration is unavailable for this workflow.";
    }
}

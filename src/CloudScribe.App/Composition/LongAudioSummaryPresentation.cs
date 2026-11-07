using System.Globalization;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Plain-language time and cost summary. It separates the list-price estimate for the article's characters
/// from the safety holds the app keeps, and never presents either as a Google bill.</summary>
public static class LongAudioSummaryPresentation
{
    public static string Text(GoogleLongAudioRunSummary summary, DateTimeOffset now, TimeSpan? currentRun, long estimatedListPriceCents)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var timing = summary.Timing;
        string time = timing is null
            ? "Time: not recorded for this older job."
            : $"Time: {Duration(timing.ElapsedAt(now))} {(timing.EarlierHistoryUnknown ? "since timing began" : "since this job started")} (includes pauses) · " +
                $"{Duration(TimeSpan.FromMilliseconds(timing.RecordedActiveMilliseconds) + (currentRun ?? TimeSpan.Zero))} actively working (includes waiting for Google)." +
                (timing.EarlierHistoryUnknown || timing.ActiveTimeIncomplete || timing.Running && currentRun is null
                    ? " Some earlier or interrupted time was not recorded, so this is not a complete total." : string.Empty);
        return time + (currentRun is { } run ? $"\nThis run so far: {Duration(run)}." : string.Empty) +
            $"\nReserved request attempts for this job: {summary.RetainedAttempts.ToString("N0", CultureInfo.InvariantCulture)} (including rejected, unknown, replacement and locally stopped attempts; not a verified count of requests received by Google)." +
            (estimatedListPriceCents > 0 ? $"\nEstimated list price for this article's characters: US${Money(estimatedListPriceCents)}." : string.Empty) +
            $"\nSafety holds kept by CloudScribe: US${Money(summary.JobRetainedCommitmentsUsdCents)} for this job, US${Money(summary.AllJobsRetainedCommitmentsUsdCents)} across all jobs; " +
            $"US${Money(summary.CumulativeProvisionUsdCents)} of your US${Money(summary.ApprovedCapUsdCents)} approved cap is reserved (including the tax/currency allowance)." +
            "\nEstimates and holds are not a Google bill. Your Google Cloud billing report shows the actual charges. This summary is also saved in the local diagnostic log when a run ends.";
    }

    public static string Duration(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}");

    private static string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}

using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class LongAudioSummaryPresentationTests
{
    [Fact]
    public void SummarySeparatesListPriceFromHoldsAndNeverClaimsABill()
    {
        var started = new DateTimeOffset(2026, 11, 7, 1, 29, 0, TimeSpan.FromHours(8));
        var timing = new GoogleLongAudioTiming(1, "root", started, started.AddMinutes(906), (long)TimeSpan.FromHours(2).TotalMilliseconds,
            false, false, false, LongAudioWorkflowStage.Completed);
        var summary = new GoogleLongAudioRunSummary(timing, 292, 649, 1242, 1300, 5000);
        string text = LongAudioSummaryPresentation.Text(summary, started.AddDays(1), null, 63);
        Assert.Contains("15:06:00 since this job started", text, StringComparison.Ordinal);
        Assert.Contains("2:00:00 actively working", text, StringComparison.Ordinal);
        Assert.Contains("Reserved request attempts for this job: 292", text, StringComparison.Ordinal);
        Assert.Contains("not a verified count of requests received by Google", text, StringComparison.Ordinal);
        Assert.Contains("Estimated list price for this article's characters: US$0.63", text, StringComparison.Ordinal);
        Assert.Contains("US$6.49 for this job", text, StringComparison.Ordinal);
        Assert.Contains("not a Google bill", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderOrInterruptedJobsSayTheTotalIsIncomplete()
    {
        var summary = new GoogleLongAudioRunSummary(null, 1, 63, 63, 70, 100);
        Assert.Contains("not recorded", LongAudioSummaryPresentation.Text(summary, DateTimeOffset.UnixEpoch, null, 0), StringComparison.Ordinal);
        var started = DateTimeOffset.UnixEpoch;
        var interrupted = new GoogleLongAudioTiming(1, "root", started, null, 1000, false, true, false, LongAudioWorkflowStage.Stopped);
        string text = LongAudioSummaryPresentation.Text(summary with { Timing = interrupted }, started.AddMinutes(5), TimeSpan.FromSeconds(30), 0);
        Assert.Contains("not a complete total", text, StringComparison.Ordinal);
        Assert.Contains("This run so far: 0:00:30", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Estimated list price", text, StringComparison.Ordinal);
    }
}

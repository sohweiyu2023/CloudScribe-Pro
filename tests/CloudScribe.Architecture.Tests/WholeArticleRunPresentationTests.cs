using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class WholeArticleRunPresentationTests
{
    [Theory]
    [InlineData(0, 4, 0d)]
    [InlineData(1, 4, 25d)]
    [InlineData(4, 4, 100d)]
    public void CoverageRepresentsVerifiedParts(int verified, int total, double expected)
    {
        Assert.Equal(expected, WholeArticleRunPresentation.CoveragePercentage(verified, total));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 4)]
    [InlineData(5, 4)]
    public void InvalidCoverageIsUnknownNotInvented(int verified, int total)
    {
        Assert.Null(WholeArticleRunPresentation.CoveragePercentage(verified, total));
    }

    [Fact]
    public void ElapsedRetainsDaysAndClearlyExcludesHistoricalTime()
    {
        string text = WholeArticleRunPresentation.ElapsedText(TimeSpan.FromHours(49) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3));
        Assert.Contains("49:02:03", text, StringComparison.Ordinal);
        Assert.Contains("This-run elapsed", text, StringComparison.Ordinal);
        Assert.Contains("includes provider waits", text, StringComparison.Ordinal);
        Assert.Contains("Earlier runs and pauses are not included", text, StringComparison.Ordinal);
        Assert.Contains("whole-job duration is unavailable", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => WholeArticleRunPresentation.ElapsedText(TimeSpan.FromSeconds(-1)));
    }
}

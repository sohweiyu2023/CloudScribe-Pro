using CloudReader.Core.Models;
using CloudReader.Core.Processing;

namespace CloudReader.Tests;

public sealed class SanitizerTests
{
    [Fact]
    public void Sanitizer_RemovesNestedBracketsAndUrls()
    {
        var sanitizer = new TextSanitizer();
        var options = new PreprocessOptions
        {
            SkipUrls = true,
            SkipRoundBrackets = true,
            SkipSquareBrackets = true,
            SkipCurlyBrackets = true
        };

        var output = sanitizer.Sanitize("See (internal [nested]) at https://example.com and {meta}", options);
        Assert.DoesNotContain("https://", output);
        Assert.DoesNotContain("nested", output);
        Assert.Contains("link omitted", output);
    }

    [Fact]
    public void Sanitizer_PreservesParagraphBreaks()
    {
        var sanitizer = new TextSanitizer();
        var output = sanitizer.Sanitize("First paragraph.\n\nSecond   paragraph.", new PreprocessOptions());

        Assert.Contains("\n\n", output);
        Assert.DoesNotContain("   ", output);
    }

    [Fact]
    public void Sanitizer_DoesNotApplySkipInsideSsmlTags_WhenEnabled()
    {
        var sanitizer = new TextSanitizer();
        var options = new PreprocessOptions
        {
            SkipSquareBrackets = true,
            ExcludeInsideSsmlTags = true
        };

        var output = sanitizer.Sanitize("<say-as interpret-as=\"characters\">[A1]</say-as> [remove me]", options);

        Assert.Contains("[A1]", output);
        Assert.DoesNotContain("[remove me]", output);
    }

    [Fact]
    public void Sanitizer_ReportsRemovedSpanMetrics()
    {
        var sanitizer = new TextSanitizer();
        var options = new PreprocessOptions
        {
            SkipUrls = true,
            SkipRoundBrackets = true
        };

        var report = sanitizer.SanitizeWithReport("Visit https://example.com (details)", options);

        Assert.True(report.RemovedSpanCount >= 2);
        Assert.True(report.CharactersRemoved > 0);
    }
}

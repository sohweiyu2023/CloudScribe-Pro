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
        Assert.Contains("[link omitted]", output);
    }
}

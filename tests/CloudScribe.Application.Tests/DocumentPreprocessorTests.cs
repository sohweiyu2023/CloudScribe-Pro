using CloudScribe.Application.Documents;

namespace CloudScribe.Application.Tests;

public sealed class DocumentPreprocessorTests
{
    [Fact]
    public void IdentityPreviewPreservesUnicodeExactly()
    {
        DocumentPreprocessor preprocessor = new();
        const string source = "明 👨‍👩‍👧‍👦 e\u0301\r\nالعربية עברית";

        DocumentPreprocessingPreview preview = preprocessor.Preview(
            source,
            new(NormalizeLineEndings: false, CollapseExcessBlankLines: false));

        Assert.Equal(source, preview.OutputText);
        Assert.NotEmpty(preview.SourceMap);
        Assert.All(preview.SourceMap, segment => Assert.Equal(segment.OutputLength, segment.SourceLength));
    }

    [Fact]
    public void PreviewMapsNormalizedLinesWhitespaceAndUrlsBackToSource()
    {
        DocumentPreprocessor preprocessor = new();
        const string source = "Visit   https://www.example.com/path?q=1\r\n\r\n\r\nNext";

        DocumentPreprocessingPreview preview = preprocessor.Preview(
            source,
            new(
                NormalizeLineEndings: true,
                CollapseHorizontalWhitespace: true,
                CollapseExcessBlankLines: true,
                SimplifyUrls: true));

        Assert.Equal("Visit example.com\n\nNext", preview.OutputText);
        Assert.Contains(preview.SourceMap, segment => string.Equals(segment.Transform, "url-simplified", StringComparison.Ordinal));
        Assert.Contains(preview.SourceMap, segment => string.Equals(segment.Transform, "horizontal-whitespace", StringComparison.Ordinal));
        Assert.Contains(preview.SourceMap, segment => string.Equals(segment.Transform, "line-ending", StringComparison.Ordinal));
        Assert.Single(preview.Warnings);
        Assert.All(preview.SourceMap, segment =>
        {
            Assert.InRange(segment.SourceStart, 0, source.Length);
            Assert.InRange(segment.SourceStart + segment.SourceLength, 0, source.Length);
            Assert.InRange(segment.OutputStart + segment.OutputLength, 0, preview.OutputText.Length);
        });
    }
}

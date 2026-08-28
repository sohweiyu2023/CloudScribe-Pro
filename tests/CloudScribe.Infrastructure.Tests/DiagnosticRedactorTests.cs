using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Tests;

public sealed class DiagnosticRedactorTests
{
    [Theory]
    [InlineData("api_key=abc123", "api_key=[REDACTED]")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization=[REDACTED]")]
    [InlineData(@"C:\Users\Alice\Documents\file.txt", @"C:\Users\[USER]\Documents\file.txt")]
    [InlineData("/Users/alice/private/file.txt", "/Users/[USER]/private/file.txt")]
    [InlineData("/home/alice/private/file.txt", "/home/[USER]/private/file.txt")]
    [InlineData("https://example.test/path?token=secret#section", "https://example.test/path[REDACTED-URI-SUFFIX]")]
    [InlineData("https://example.test/path#private", "https://example.test/path[REDACTED-URI-SUFFIX]")]
    [InlineData("Contact alice@example.test", "Contact [EMAIL]")]
    [InlineData("file:///home/alice/private.txt", "file://[PATH]")]
    public void RedactsSensitivePatterns(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticRedactor.Sanitize(input));
    }

    [Fact]
    public void RemovesControlCharacters()
    {
        Assert.Equal("line one line two", DiagnosticRedactor.Sanitize("line one\r\nline two"));
    }

    [Fact]
    public void BoundsInputBeforeRegexProcessing()
    {
        string input = "https://example.test/path?token=" + new string('x', 1_000_000);

        string result = DiagnosticRedactor.Sanitize(input);

        Assert.True(result.Length <= 1025);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED-URI-SUFFIX]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsEveryField()
    {
        string result = DiagnosticRedactor.Sanitize(new string('x', 2048));

        Assert.Equal(1025, result.Length);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationNeverSplitsUtf16SurrogatePairs()
    {
        string input = new string('x', 4095) + "😀" + new string('y', 32);

        string result = DiagnosticRedactor.Sanitize(input);

        Assert.EndsWith("…", result, StringComparison.Ordinal);
        Assert.DoesNotContain(result, static character => char.IsSurrogate(character));
    }
}

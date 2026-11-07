using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class AudioOutputPresentationTests
{
    [Theory]
    [InlineData("Anatta / 中文", "Anatta _ 中文.mp3")]
    [InlineData("CON", "_CON.mp3")]
    [InlineData("nul.backup", "_nul.backup.mp3")]
    [InlineData("COM\u00B9", "_COM\u00B9.mp3")]
    [InlineData("com0", "_com0.mp3")]
    [InlineData("LPT0.notes", "_LPT0.notes.mp3")]
    [InlineData("  .  ", "complete-article.mp3")]
    [InlineData("one\ntwo", "one_two.mp3")]
    public void NamesPreserveTitlesWhileRespectingWindowsRules(string title, string expected)
    {
        Assert.Equal(expected, AudioOutputPresentation.SuggestedFileName(title, ".mp3"));
    }

    [Fact]
    public void OnlyFilesystemComponentLimitShortensLongTitles()
    {
        Assert.Equal(new string('a', 250) + ".mp3", AudioOutputPresentation.SuggestedFileName(new string('a', 250), ".mp3"));
        string result = AudioOutputPresentation.SuggestedFileName(new string('a', 250) + "😀tail", ".mp3");
        Assert.Equal(new string('a', 250) + ".mp3", result);
        Assert.True(result.Length <= 255);
    }

    [Fact]
    public void FailureAdviceIsActionableWithoutEchoingPrivateDetails()
    {
        const string secret = "private-path credential-content";
        var errors = new Exception[] { new UnauthorizedAccessException(secret), new PathTooLongException(secret), new FileNotFoundException(secret), new InvalidDataException(secret), new IOException(secret), new InvalidOperationException(secret) };
        foreach (var error in errors)
        {
            string advice = AudioOutputPresentation.RecoveryAdvice(error);
            Assert.NotEmpty(advice);
            Assert.DoesNotContain(secret, advice, StringComparison.Ordinal);
        }
        Assert.Contains("do not regenerate", AudioOutputPresentation.RecoveryAdvice(new InvalidDataException()), StringComparison.Ordinal);
    }
}

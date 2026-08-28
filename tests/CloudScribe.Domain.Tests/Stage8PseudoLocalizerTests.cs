using CloudScribe.Domain.Localization;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8PseudoLocalizerTests
{
    [Fact]
    public void PreservesCompositePlaceholdersAndExpandsLiteralText()
    {
        var result = PseudoLocalizer.Transform("Hello {name}, {0:N2}!", expansionRatio: 0.5);

        Assert.StartsWith("[!! ", result, StringComparison.Ordinal);
        Assert.Contains("{name}", result, StringComparison.Ordinal);
        Assert.Contains("{0:N2}", result, StringComparison.Ordinal);
        Assert.Contains("~", result, StringComparison.Ordinal);
        Assert.EndsWith(" !!]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeCombiningSequencesSurviveTransformation()
    {
        var input = "Cafe\u0301 👩‍💻 {count}";
        var result = PseudoLocalizer.Transform(input, 0);

        Assert.Contains("👩‍💻", result, StringComparison.Ordinal);
        Assert.Contains("\u0301", result, StringComparison.Ordinal);
        Assert.Contains("{count}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UnterminatedPlaceholderFailsClosed()
    {
        Assert.Throws<FormatException>(() => PseudoLocalizer.Transform("Hello {name"));
    }
}

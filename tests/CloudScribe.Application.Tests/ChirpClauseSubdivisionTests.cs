using System.Text;
using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class ChirpClauseSubdivisionTests
{
    [Theory]
    [InlineData("甲，乙。丙；丁：戊！己？\n庚", 7)]
    [InlineData(" 甲，\r\n乙。  ", 2)]
    [InlineData("甲，\"乙。\" 丙。", 3)]
    [InlineData("甲😀，e\u0301乙。", 2)]
    public void ExactExistingBoundariesPreserveEveryScalarAndWhitespace(string text, int count)
    {
        string source = "前" + text + "后";
        var parent = new ChirpNarrationRange(1, 1 + text.EnumerateRunes().Count(), ArticleAudioManifest.HashText(text), null);
        var parts = ChirpClauseSubdivision.Plan(source, parent);
        Assert.Equal(count, parts.Count);
        Assert.Equal(text, string.Concat(parts.Select(x => ChirpNarrationPlanner.Slice(source, x))));
        Assert.Equal(parent.StartScalar, parts[0].StartScalar);
        Assert.Equal(parent.EndScalarExclusive, parts[^1].EndScalarExclusive);
        Assert.All(parts, x => { Assert.Null(x.ReusedReceiptSha256); Assert.False(string.IsNullOrWhiteSpace(ChirpNarrationPlanner.Slice(source, x))); });
    }

    [Theory]
    [InlineData("甲乙丙")]
    [InlineData("甲乙丙。")]
    [InlineData("甲，\u0301乙。")]
    public void MissingSmallerBoundaryOrSplitTextElementFailsClosed(string text)
    {
        var range = new ChirpNarrationRange(0, text.EnumerateRunes().Count(), ArticleAudioManifest.HashText(text), null);
        Assert.Throws<InvalidOperationException>(() => ChirpClauseSubdivision.Plan(text, range));
    }

    [Fact]
    public void AcceptedCoverageAndChangedHashAreRejected()
    {
        const string text = "甲，乙。";
        var parent = new ChirpNarrationRange(0, 4, ArticleAudioManifest.HashText(text), null);
        Assert.Throws<InvalidOperationException>(() => ChirpClauseSubdivision.Plan(text, parent with { ReusedReceiptSha256 = new string('a', 64) }));
        Assert.Throws<InvalidOperationException>(() => ChirpClauseSubdivision.Plan(text, parent with { TextSha256 = new string('b', 64) }));
    }

    [Fact]
    public void PathologicalPartCountAndUnsplitLongClauseAreBounded()
    {
        string many = string.Concat(Enumerable.Repeat("甲，", 201));
        Assert.Throws<InvalidOperationException>(() => ChirpClauseSubdivision.Plan(many, new(0, many.Length, ArticleAudioManifest.HashText(many), null)));
        string longClause = new string('甲', 334) + "，乙。";
        Assert.Throws<InvalidOperationException>(() => ChirpClauseSubdivision.Plan(longClause, new(0, longClause.Length, ArticleAudioManifest.HashText(longClause), null)));
    }
}

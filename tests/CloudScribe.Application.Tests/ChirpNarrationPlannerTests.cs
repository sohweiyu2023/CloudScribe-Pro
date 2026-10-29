using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class ChirpNarrationPlannerTests
{
    [Theory]
    [InlineData("中文，保留原文。\r\n", 80)]
    [InlineData("Emoji 👩‍💻 and e\u0301; keep every scalar! ", 50)]
    [InlineData("a\tword: another word?\n", 80)]
    public void CoverageIsDeterministicAndLossless(string unit, int repetitions)
    {
        string source = string.Concat(Enumerable.Repeat(unit, repetitions));
        var result = ChirpNarrationPlanner.Plan(source);
        Assert.Equal(result.ToArray(), ChirpNarrationPlanner.Plan(source).ToArray());
        Assert.Equal(source, string.Concat(result.Select(x => ChirpNarrationPlanner.Slice(source, x))));
        Assert.Equal(source, string.Concat(ChirpNarrationPlanner.SliceAll(source, result)));
        Assert.All(result, x => Assert.InRange(x.EndScalarExclusive - x.StartScalar, 1, 333));
        Assert.All(result, x => Assert.Null(x.ReusedReceiptSha256));
    }

    [Fact]
    public void ExistingAcceptedCoverageIsReusedExactlyOnceAndNotReplanned()
    {
        string source = string.Concat(Enumerable.Repeat("文，", 600));
        string accepted = source[400..800];
        var receipt = new string('a', 64);
        var result = ChirpNarrationPlanner.Plan(source, [new(400, 800, ArticleAudioManifest.HashText(accepted), receipt)]);
        var reused = Assert.Single(result, x => x.ReusedReceiptSha256 is not null);
        Assert.Equal((400, 800, receipt), (reused.StartScalar, reused.EndScalarExclusive, reused.ReusedReceiptSha256));
        Assert.DoesNotContain(result.Where(x => x.ReusedReceiptSha256 is null), x => x.StartScalar < 800 && x.EndScalarExclusive > 400);
        Assert.Equal(source, string.Concat(result.Select(x => ChirpNarrationPlanner.Slice(source, x))));
    }

    [Theory]
    [InlineData(334)]
    [InlineData(700)]
    public void UnbrokenTextBlocksRatherThanChangingTextOrHardCutting(int length) =>
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(new string('文', length)));

    [Fact]
    public void ShortUnbrokenTextIsOneUnchangedProposal()
    {
        string source = new('文', 333);
        Assert.Equal(source, ChirpNarrationPlanner.Slice(source, Assert.Single(ChirpNarrationPlanner.Plan(source))));
    }

    [Fact]
    public void AmbiguousAsciiPeriodDoesNotInventAWordOrSentenceBoundary() =>
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(new string('a', 300) + "." + new string('b', 40)));

    [Fact]
    public void ExcessiveReuseMetadataIsRejectedBeforeMaterialization()
    {
        var item = new ChirpNarrationReuse(0, 1, ArticleAudioManifest.HashText("文"), new string('a', 64));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan("文",
            Enumerable.Repeat(item, ChirpNarrationPlanner.MaximumCoverageParts + 1).ToArray()));
    }

    [Fact]
    public void LastNearWindowPunctuationIsPreferredOverLaterWhitespace()
    {
        string source = new string('文', 300) + "," + new string('文', 20) + " " + new string('文', 20) + ",end";
        Assert.Equal(301, ChirpNarrationPlanner.Plan(source)[0].EndScalarExclusive);
    }

    [Fact]
    public void EarlierExistingBoundaryAndClosingQuotesArePreserved()
    {
        string source = new string('文', 200) + ",”" + new string('文', 200);
        var result = ChirpNarrationPlanner.Plan(source);
        Assert.Equal(202, result[0].EndScalarExclusive);
        Assert.Equal(source, string.Concat(result.Select(x => ChirpNarrationPlanner.Slice(source, x))));
    }

    [Fact]
    public void InvalidUnicodeIsRejectedWithoutNormalization()
    {
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan("before\ud800after"));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan("before\udc00after"));
    }

    [Fact]
    public void InvalidOrOverlappingReuseAndChangedSourceFailClosed()
    {
        string source = new('文', 300);
        var reuse = new ChirpNarrationReuse(0, 100, ArticleAudioManifest.HashText(source[..100]), new string('a', 64));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(source, [reuse, reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(source, [reuse with { EndScalarExclusive = 301 }]));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(source, [reuse with { TextSha256 = new string('b', 64) }]));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(source, [reuse with { ReceiptSha256 = "bad" }]));
        var part = Assert.Single(ChirpNarrationPlanner.Plan(source));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Slice(new string('字', 300), part));
    }

    [Fact]
    public void AcceptedCoverageCannotSplitEmojiOrCombiningSequence()
    {
        string source = "e\u0301,👩‍💻,end";
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.Plan(source,
            [new(0, 1, ArticleAudioManifest.HashText("e"), new string('a', 64))]));
    }

    [Fact]
    public void BulkCoverageRejectsMissingDuplicatedReorderedStaleOrTruncatedRanges()
    {
        string source = string.Concat(Enumerable.Repeat("文，", 600));
        var ranges = ChirpNarrationPlanner.Plan(source).ToArray();
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.SliceAll(source, ranges.Skip(1).ToArray()));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.SliceAll(source, ranges.Append(ranges[0]).ToArray()));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.SliceAll(source, ranges.Reverse().ToArray()));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.SliceAll(source, ranges[..^1]));
        Assert.Throws<InvalidOperationException>(() => ChirpNarrationPlanner.SliceAll(source.Replace('文', '字'), ranges));
    }
}

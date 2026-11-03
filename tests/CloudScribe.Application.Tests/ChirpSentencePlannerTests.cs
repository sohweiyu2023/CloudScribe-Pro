using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class ChirpSentencePlannerTests
{
    [Theory]
    [InlineData("\u7532,\u4e59\u3002\r\n", 120)]
    [InlineData("e\u0301 and \U0001f469\u200d\U0001f4bb?\n", 100)]
    public void GroupsWholeSentencesDeterministicallyAndPreservesExactSource(string unit, int count)
    {
        string source = string.Concat(Enumerable.Repeat(unit, count));
        var plan = ChirpSentencePlanner.Plan(source, []);
        Assert.Equal(plan.ToArray(), ChirpSentencePlanner.Plan(source, []).ToArray());
        Assert.Equal(source, string.Concat(ChirpNarrationPlanner.SliceAll(source, plan)));
        Assert.All(plan, x => Assert.InRange(x.EndScalarExclusive - x.StartScalar, 1, 333));
        Assert.All(plan.Take(plan.Count - 1), x => Assert.EndsWith(unit, ChirpNarrationPlanner.Slice(source, x), StringComparison.Ordinal));
    }

    [Fact]
    public void InheritedPartialSentenceIsIsolatedAndAcceptedRangeNeverRegenerated()
    {
        const string prefix = "accepted,"; const string first = "remaining first sentence.\u3002 ";
        string source = prefix + first + string.Concat(Enumerable.Repeat("another whole sentence\u3002", 40));
        var reuse = new ChirpNarrationReuse(0, prefix.Length, ArticleAudioManifest.HashText(prefix), new string('a', 64));
        var plan = ChirpSentencePlanner.Plan(source, [reuse]);
        Assert.Equal(reuse.ReceiptSha256, plan[0].ReusedReceiptSha256);
        Assert.Equal(first, ChirpNarrationPlanner.Slice(source, plan[1]));
        Assert.Equal(source, string.Concat(ChirpNarrationPlanner.SliceAll(source, plan)));
        Assert.Throws<InvalidOperationException>(() => ChirpSentencePlanner.Plan(source, [reuse, reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpSentencePlanner.Plan(source, [reuse with { TextSha256 = new string('b', 64) }]));
    }

    [Fact]
    public void OversizedSentenceAndGraphemeSplittingReuseFailWithoutClauseFallback()
    {
        Assert.Throws<InvalidOperationException>(() => ChirpSentencePlanner.Plan(new string('x', 334) + ", short\u3002", []));
        const string source = "e\u0301\u3002 next\u3002";
        Assert.Throws<InvalidOperationException>(() => ChirpSentencePlanner.Plan(source,
            [new(0, 1, ArticleAudioManifest.HashText("e"), new string('a', 64))]));
    }
}

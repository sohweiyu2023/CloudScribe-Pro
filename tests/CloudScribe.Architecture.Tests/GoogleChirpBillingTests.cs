using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleChirpBillingTests
{
    private static GoogleTtsStudioCostEstimator.EstimateResult Estimate(string text)
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        return GoogleTtsStudioCostEstimator.Estimate(controls, "cmn-CN-Chirp3-HD-Alnilam",
            JsonSerializer.SerializeToUtf8Bytes(new { input = new { text } }), "global");
    }

    [Theory]
    [InlineData("\u4E2D\u6587", 2, 6)]
    [InlineData("\U0001F600", 1, 4)]
    [InlineData("e\u0301", 2, 3)]
    [InlineData(" a\n", 3, 3)]
    [InlineData("<mark name=\"x\"/>", 16, 16)]
    public void PlainTextCountsUnicodeScalarsWhitespaceAndLiteralMarkup(string text, long characters, long bytes)
    {
        var estimate = Estimate(text);
        Assert.Equal(characters, estimate.EstimatedBillableCharacters);
        Assert.Equal(bytes, estimate.InputUtf8Bytes);
        Assert.Equal(1, estimate.MaximumMinorUnits);
    }

    [Theory]
    [InlineData(333, 1)]
    [InlineData(334, 2)]
    [InlineData(1000, 3)]
    public void ChineseRoundsEachRequestUpwardsWithoutUsingByteCount(int count, long cents)
    {
        var estimate = Estimate(new string('\u4E2D', count));
        Assert.Equal(count, estimate.EstimatedBillableCharacters);
        Assert.Equal(count * 3L, estimate.InputUtf8Bytes);
        Assert.Equal(cents, estimate.MaximumMinorUnits);
        Assert.Equal(2, estimate.Scale);
    }

    [Fact]
    public void ChunkCeilingsAreNotSilentlyReplacedByAggregateListQuote()
    {
        Assert.Equal(2, Estimate(new string('\u4E2D', 100)).MaximumMinorUnits * 2);
        Assert.Equal(1, Estimate(new string('\u4E2D', 200)).MaximumMinorUnits);
    }

    [Fact]
    public void ChirpSsmlAndInvalidSurrogatesFailClosed()
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        byte[] ssml = JsonSerializer.SerializeToUtf8Bytes(new { input = new { ssml = "<speak>\u4E2D<mark name=\"x\"/></speak>" } });
        Assert.Throws<InvalidOperationException>(() => GoogleTtsStudioCostEstimator.Estimate(controls, "cmn-CN-Chirp3-HD-Alnilam", ssml, "global"));
        foreach (string escaped in new[] { "\\uD800", "\\uDC00" })
        {
            byte[] payload = Encoding.UTF8.GetBytes("{\"input\":{\"text\":\"" + escaped + "\"}}");
            Assert.Throws<InvalidOperationException>(() => GoogleTtsStudioCostEstimator.Estimate(controls, "cmn-CN-Chirp3-HD-Alnilam", payload, "global"));
        }
    }

    [Fact]
    public void JsonEscapingDoesNotBillEnvelopeBytesAndLegacyFamilyUpperBoundIsUnchanged()
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        byte[] payload = Encoding.UTF8.GetBytes("{\"input\":{\"text\":\"\\u4E2D\\u6587\"}}");
        var chirp = GoogleTtsStudioCostEstimator.Estimate(controls, "cmn-CN-Chirp3-HD-Alnilam", payload, "global");
        Assert.Equal(2, chirp.EstimatedBillableCharacters);
        var legacy = GoogleTtsStudioCostEstimator.Estimate(controls, "cmn-CN-Wavenet-A", payload, "global");
        Assert.Equal(6, legacy.EstimatedBillableCharacters);
    }
}

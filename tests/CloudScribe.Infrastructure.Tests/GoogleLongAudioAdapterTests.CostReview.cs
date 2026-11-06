using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public void InputByteLimitDoesNotIncorrectlyLimitEscapedJsonWireBytes()
    {
        var request = Request(text: new string('\u6587', 300_000));
        Assert.Equal(900_000, request.InputUtf8Bytes); Assert.True(request.PayloadBytes > 1_000_000);
        Assert.Throws<ArgumentException>(() => Request(text: new string('\u6587', 333_334)));
    }

    [Fact]
    public void CostReviewPreservesCumulativeCommitmentsAndCountsInputSeparatelyFromWireBytes()
    {
        var request = Request(text: "\u4f60\u597d" + char.ConvertFromUtf32(0x1f600));
        var review = GoogleLongAudioCostReview.Prepare(request, 30m, 553, 200, 1000, "synthetic pricing fixture");
        Assert.Equal(10, review.InputUtf8Bytes); Assert.Equal(4, review.ConservativeCharacterUnits);
        Assert.True(review.WireBytes > review.InputUtf8Bytes);
        Assert.Equal(1, review.EstimatedSynthesisUsdCents); Assert.Equal(754, review.ProjectedProvisionUsdCents);
        Assert.True(review.WithinProvidedCap); Assert.Equal(request.BindingSha256, review.RequestBindingSha256);
    }

    [Fact]
    public void CostReviewCannotHideHistoricalSpendOrOverflowTheCapCalculation()
    {
        var request = Request(text: new string('x', 100_000));
        var review = GoogleLongAudioCostReview.Prepare(request, 30m, 553, 200, 1000, "synthetic pricing fixture");
        Assert.Equal(300, review.EstimatedSynthesisUsdCents); Assert.Equal(1053, review.ProjectedProvisionUsdCents);
        Assert.False(review.WithinProvidedCap);
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleLongAudioCostReview.Prepare(request, 30m, -1, 200, 1000, "fixture"));
        Assert.Throws<OverflowException>(() => GoogleLongAudioCostReview.Prepare(request, 30m, long.MaxValue, 200, 1000, "fixture"));
    }
}

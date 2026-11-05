using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class WholeArticleReviewPresentationTests
{
    [Fact]
    public void AutomaticRecoveryEstimateAndPrivacyBoundsAreExplicitWithoutPerChildConsent()
    {
        var review = Review() with { MaximumRecoveryRequests = 32, MaximumRecoveryMinorUnits = 64, MaximumAdditionalMinorUnits = 81 };
        string estimate = WholeArticleReviewPresentation.Estimate(review);
        Assert.Contains("up to USD 0.81", estimate, StringComparison.Ordinal);
        Assert.Contains("32 additional source-preserving split requests", estimate, StringComparison.Ordinal);
        Assert.Contains("USD 0.64", estimate, StringComparison.Ordinal);
        Assert.Contains("does not guarantee acceptance", estimate, StringComparison.Ordinal);
        string capture = WholeArticleReviewPresentation.CaptureDescription(review);
        Assert.Contains("without a per-error count limit", capture, StringComparison.Ordinal);
        Assert.Contains("All private error history is preserved for 24 hours without count-based rotation", capture, StringComparison.Ordinal);
        Assert.Contains("disk or storage failure", capture, StringComparison.Ordinal);
        Assert.Contains("no capture after restart", capture, StringComparison.Ordinal);
        Assert.Contains("Capture itself grants no paid permission", capture, StringComparison.Ordinal);
        Assert.Contains("first failing response", WholeArticleReviewPresentation.CaptureDescription(Review()), StringComparison.Ordinal);
    }

    private static ChirpWholeArticleReview Review(int scale = 2) => new(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(),
        new string('b', 64), "synthetic-voice", "USD", scale, 23, 25, 2, 17, 175,
        DateTimeOffset.UnixEpoch.AddHours(1), 2, DateTimeOffset.UnixEpoch.AddMinutes(10));

    [Fact]
    public void OneEstimateIsExplicitlyNotAFinalBillOrPerPartApproval()
    {
        string text = WholeArticleReviewPresentation.Estimate(Review());
        Assert.Contains("up to USD 0.17", text, StringComparison.Ordinal);
        Assert.Contains("Generate authorizes up to this amount", text, StringComparison.Ordinal);
        Assert.Contains("Existing commitments: USD 1.75", text, StringComparison.Ordinal);
        Assert.Contains("acknowledges these existing holds", text, StringComparison.Ordinal);
        Assert.Contains("not the final Google bill", text, StringComparison.Ordinal);
        Assert.Contains("without separate confirmations", text, StringComparison.Ordinal);
        Assert.Contains("existing approved spending limit", text, StringComparison.Ordinal);
        Assert.Contains("uncertain outcome", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EstimateUsesDeclaredCurrencyScale()
    {
        Assert.Contains("USD 0.017", WholeArticleReviewPresentation.Estimate(Review(3)), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingCommitmentsAndAuthorityRemainExplicit()
    {
        string text = WholeArticleReviewPresentation.Details(Review());
        Assert.Contains("does not raise the configured spending ceiling", text, StringComparison.Ordinal);
        Assert.Contains("clear an unresolved charge", text, StringComparison.Ordinal);
    }
}

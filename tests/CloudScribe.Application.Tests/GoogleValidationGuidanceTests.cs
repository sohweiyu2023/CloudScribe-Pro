using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class GoogleValidationGuidanceTests
{
    [Fact]
    public void SentenceHintOffersFixedReviewGuidanceWithoutApprovingRetry()
    {
        string result = GoogleValidationGuidance.For("google-http-400", GoogleErrorStatus.InvalidArgument,
            GoogleValidationMessageHint.SentenceLengthReported);
        Assert.Contains("previous holds", result, StringComparison.Ordinal);
        Assert.Contains("fresh exact approval", result, StringComparison.Ordinal);
        Assert.Contains("do not retry automatically", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("google-http-503", GoogleErrorStatus.InvalidArgument, GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("google-http-400", GoogleErrorStatus.Unknown, GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("google-http-400", GoogleErrorStatus.InvalidArgument, GoogleValidationMessageHint.Unrecognized)]
    [InlineData("PRIVATE_ECHO", GoogleErrorStatus.InvalidArgument, GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("google-http-400", GoogleErrorStatus.InvalidArgument, (GoogleValidationMessageHint)999)]
    public void UnrelatedOrUnrecognizedEvidenceDoesNotInventSentenceCause(string diagnostic, GoogleErrorStatus status, GoogleValidationMessageHint hint) =>
        Assert.Empty(GoogleValidationGuidance.For(diagnostic, status, hint));
}

using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class CreateMp3NextStepTests
{
    [Fact]
    public void EmptyDocumentExplainsTheFirstActionAndAllowsPublicSamples()
    {
        string message = CreateMp3NextStep.Describe(false, false, false, false, false, true, false, false, false);
        Assert.Contains("write or import text", message, StringComparison.Ordinal);
        Assert.Contains("public voice samples", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVoicePointsToCatalogRecoveryInsteadOfApproval()
    {
        string message = CreateMp3NextStep.Describe(false, false, true, false, false, true, false, false, false);
        Assert.Contains("connection/filter", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewedRequestStillExplainsRequiredConsentAndBilling()
    {
        string message = CreateMp3NextStep.Describe(false, false, true, false, true, true, true, false, true);
        Assert.Contains("approval box", message, StringComparison.Ordinal);
        Assert.Contains("may charge", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredOrUsedApprovalRequiresANewReview()
    {
        string message = CreateMp3NextStep.Describe(false, false, true, false, true, true, true, true, false);
        Assert.Contains("review again", message, StringComparison.Ordinal);
    }
}

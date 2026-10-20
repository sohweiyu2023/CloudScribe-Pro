using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class WalkthroughPlanTests
{
    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 11)]
    public void RoutesCoverWholeJourneyWithStableAnchors(bool saved, int count)
    {
        var steps = WalkthroughPlan.Build(saved);
        Assert.Equal(count, steps.Count);
        Assert.Equal(WalkthroughScreen.Studio, steps[0].Screen);
        Assert.Equal(WalkthroughScreen.Return, steps[^1].Screen);
        Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Target)));
        Assert.Contains(steps, step => string.Equals(step.Target, "Voice", StringComparison.Ordinal));
        Assert.Contains(steps, step => string.Equals(step.Target, "Review current text, voice and cost", StringComparison.Ordinal));
        Assert.Contains(steps, step => string.Equals(step.Target, "I approve this exact request and its displayed maximum cost.", StringComparison.Ordinal));
        Assert.Contains(steps, step => string.Equals(step.Target, "Listen, save and find audio", StringComparison.Ordinal));
        Assert.Equal(saved ? 0 : 4, steps.Count(step => step.Screen == WalkthroughScreen.Connection));
        Assert.All(steps.Where(step => step.Link is not null), step => Assert.StartsWith("https://cloud.google.com/", step.Link, StringComparison.Ordinal));
    }

    [Fact]
    public void AboutMatchesInspectedReferenceWithCloudScribeIdentity()
    {
        Assert.Equal("sohweiyu@outlook.com", CloudScribeAbout.PayPalEmail);
        Assert.Equal("https://www.awakeningtoreality.com/", CloudScribeAbout.Website);
        Assert.Equal("Created and maintained by Soh Wei Yu", CloudScribeAbout.Credit);
        Assert.Equal("CloudScribe Pro is free to use. If it saves you time and you would like to support continued maintenance, an optional PayPal contribution is appreciated - never required.", CloudScribeAbout.SupportMessage);
    }
}

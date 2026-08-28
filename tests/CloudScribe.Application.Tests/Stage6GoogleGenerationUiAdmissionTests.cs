using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleGenerationUiAdmissionTests
{
    private static readonly GoogleGenerationUiSelection Valid = new(
        "acct-1", "project-1", "voice-1", "model-1", "cap-1", "wav");

    [Fact]
    public void CurrentAuthorizedSelectionIsAdmitted()
    {
        var admitted = GoogleGenerationUiAdmission.RequireCurrent(Valid, true, true, true, true);
        Assert.Same(Valid, admitted);
    }

    [Fact]
    public void StalePricingOrCapabilityFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationUiAdmission.RequireCurrent(Valid, true, true, false, true));
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationUiAdmission.RequireCurrent(Valid, true, true, true, false));
    }

    [Fact]
    public void NoncanonicalIdentityFailsClosed()
    {
        var bad = Valid with { VoiceId = " voice-1" };
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationUiAdmission.RequireCurrent(bad, true, true, true, true));
    }
}

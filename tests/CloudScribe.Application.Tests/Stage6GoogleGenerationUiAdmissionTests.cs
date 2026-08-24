using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleGenerationUiAdmissionTests
{
    private static readonly GoogleGenerationUiSelection Valid = new(
        "acct-1", "project-1", "voice-1", "model-1", "cap-1", "wav");

    [Fact]
    public void Current_authorized_selection_is_admitted()
    {
        var admitted = GoogleGenerationUiAdmission.RequireCurrent(Valid, true, true, true, true);
        Assert.Same(Valid, admitted);
    }

    [Fact]
    public void Stale_pricing_or_capability_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationUiAdmission.RequireCurrent(Valid, true, true, false, true));
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationUiAdmission.RequireCurrent(Valid, true, true, true, false));
    }

    [Fact]
    public void Noncanonical_identity_fails_closed()
    {
        var bad = Valid with { VoiceId = " voice-1" };
        Assert.Throws<InvalidOperationException>(() => GoogleGenerationUiAdmission.RequireCurrent(bad, true, true, true, true));
    }
}

using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceCacheIsolationPolicyTests
{
    [Fact]
    public void PrivateVoiceNeverReusesAcrossAccountOrProject()
    {
        var source = Scope("account-a", "project-a", privateVoice: true);
        var otherAccount = Scope("account-b", "project-a", privateVoice: true);
        var otherProject = Scope("account-a", "project-b", privateVoice: true);

        Assert.False(source.CanReuseWith(otherAccount, explicitCurrentCrossAccountEquivalence: true));
        Assert.False(source.CanReuseWith(otherProject, explicitCurrentCrossAccountEquivalence: true));
    }

    [Fact]
    public void PublicVoiceCrossAccountReuseRequiresExplicitCurrentEquivalence()
    {
        var source = Scope("account-a", "project-a", privateVoice: false);
        var destination = Scope("account-b", "project-b", privateVoice: false);

        Assert.False(source.CanReuseWith(destination));
        Assert.True(source.CanReuseWith(destination, explicitCurrentCrossAccountEquivalence: true));
    }

    [Fact]
    public void VoiceFingerprintDriftAlwaysInvalidatesReuse()
    {
        var source = Scope("account-a", "project-a", privateVoice: false);
        var changed = source with { VoiceFingerprint = "fingerprint-b" };

        Assert.False(source.CanReuseWith(changed, explicitCurrentCrossAccountEquivalence: true));
    }

    private static VoiceCacheIsolationScope Scope(string account, string project, bool privateVoice) =>
        new(
            "provider/test",
            account,
            project,
            "voice/test",
            "fingerprint-a",
            privateVoice);
}

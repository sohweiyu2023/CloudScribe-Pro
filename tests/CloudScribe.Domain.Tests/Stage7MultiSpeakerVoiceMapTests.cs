using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7MultiSpeakerVoiceMapTests
{
    [Fact]
    public void ResolveRequiresExactPinnedSpeakerRole()
    {
        var map = new MultiSpeakerVoiceMap([
            Binding("narrator", "provider-a", "account-a", "voice-a", "price-1", "cap-1"),
            Binding("guest", "provider-b", "account-b", "voice-b", "price-2", "cap-2"),
        ]).Validate();

        Assert.Equal("voice-b", map.Resolve("guest").VoiceStableId);
        Assert.Throws<KeyNotFoundException>(() => map.Resolve("unknown"));
    }

    [Fact]
    public void DuplicateSpeakerRoleIsRejected()
    {
        var map = new MultiSpeakerVoiceMap([
            Binding("narrator", "provider-a", "account-a", "voice-a", "price-1", "cap-1"),
            Binding("narrator", "provider-b", "account-b", "voice-b", "price-2", "cap-2"),
        ]);

        Assert.Throws<InvalidOperationException>(map.Validate);
    }

    [Fact]
    public void RouteOrProvenanceChangeCannotOccurSilently()
    {
        var before = new MultiSpeakerVoiceMap([
            Binding("narrator", "provider-a", "account-a", "voice-a", "price-1", "cap-1"),
        ]).Validate();
        var changed = new MultiSpeakerVoiceMap([
            Binding("narrator", "provider-a", "account-a", "voice-a", "price-2", "cap-1"),
        ]).Validate();

        Assert.Throws<InvalidOperationException>(() => changed.AssertNoSilentRouteChange(before));
    }

    private static SpeakerVoiceBinding Binding(string role, string provider, string account, string voice, string price, string capability) =>
        new(role, provider, account, voice, price, capability);
}

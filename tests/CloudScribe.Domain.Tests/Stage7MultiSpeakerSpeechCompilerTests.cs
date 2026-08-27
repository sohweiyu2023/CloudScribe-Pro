using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7MultiSpeakerSpeechCompilerTests
{
    [Fact]
    public void Compile_ProducesDeterministicPinnedTurns()
    {
        var plan = new SpeechPlan("en-US", new SpeechPlanNode[]
        {
            new SpeechSpeakerChange("narrator"),
            new SpeechText("Hello."),
            new SpeechPause(TimeSpan.FromMilliseconds(100)),
            new SpeechSpeakerChange("guest"),
            new SpeechText("Hi."),
        }, "plan-prov");
        var map = new MultiSpeakerVoiceMap(new[]
        {
            new SpeakerVoiceBinding("narrator", "provider-a", "account-a", "voice-a", "price-a", "cap-a"),
            new SpeakerVoiceBinding("guest", "provider-b", "account-b", "voice-b", "price-b", "cap-b"),
        });

        var turns = MultiSpeakerSpeechCompiler.Compile(plan, map);

        Assert.Equal(2, turns.Count);
        Assert.Equal("provider-a/account-a/voice-a", turns[0].Voice.RouteIdentity);
        Assert.Equal("provider-b/account-b/voice-b", turns[1].Voice.RouteIdentity);
        Assert.Equal(new[] { 1, 4 }, turns.Select(static t => t.StartNodeIndex));
    }

    [Fact]
    public void Compile_RejectsSpeakableContentBeforeSpeaker()
    {
        var plan = new SpeechPlan("en-US", new SpeechPlanNode[] { new SpeechText("Unsafe") }, "p");
        var map = new MultiSpeakerVoiceMap(new[]
        {
            new SpeakerVoiceBinding("narrator", "provider", "account", "voice", "price", "cap"),
        });
        Assert.Throws<InvalidOperationException>(() => MultiSpeakerSpeechCompiler.Compile(plan, map));
    }

    [Fact]
    public void Compile_RejectsInlineVoiceOverride()
    {
        var plan = new SpeechPlan("en-US", new SpeechPlanNode[]
        {
            new SpeechSpeakerChange("narrator"),
            new SpeechVoice("narrator", "different-voice"),
            new SpeechText("No silent reroute."),
        }, "p");
        var map = new MultiSpeakerVoiceMap(new[]
        {
            new SpeakerVoiceBinding("narrator", "provider", "account", "voice", "price", "cap"),
        });
        Assert.Throws<InvalidOperationException>(() => MultiSpeakerSpeechCompiler.Compile(plan, map));
    }
}

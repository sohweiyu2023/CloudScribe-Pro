using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleSpeechPlanCompilerTests
{
    [Fact]
    public void Compile_IsDeterministic_AndReportsUnsupportedCanonicalFeatures()
    {
        var plan = new SpeechPlan(
            "en-US",
            [
                new SpeechText("Hello"),
                new SpeechPause(TimeSpan.FromMilliseconds(250)),
                new SpeechPronunciation("world", "ipa", "wɜːld"),
                new SpeechMark("m1"),
                new SpeechProsody(1.1m, 2m, -1m),
            ],
            "prov-1");
        var options = new GoogleSpeechCompilationOptions("en-US", "en-US-Test", "LINEAR16", 4096);

        var first = GoogleSpeechPlanCompiler.Compile(plan, options);
        var second = GoogleSpeechPlanCompiler.Compile(plan, options);

        Assert.Equal(first.PayloadSha256, second.PayloadSha256);
        Assert.Equal(first.Payload.ToArray(), second.Payload.ToArray());
        Assert.Equal(3, first.Degradations.Count);
        using var json = JsonDocument.Parse(first.Payload);
        Assert.Equal("Hello world", json.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.Equal(1.1m, json.RootElement.GetProperty("audioConfig").GetProperty("speakingRate").GetDecimal());
    }

    [Fact]
    public void Compile_FailsClosed_WhenPostCompilePayloadExceedsAdmittedLimit()
    {
        var plan = new SpeechPlan("en-US", [new SpeechText(new string('x', 1000))], "prov-2");
        var options = new GoogleSpeechCompilationOptions("en-US", "en-US-Test", "LINEAR16", 256);

        var error = Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.Compile(plan, options));

        Assert.Contains("post-compile limit", error.Message, StringComparison.Ordinal);
    }
}

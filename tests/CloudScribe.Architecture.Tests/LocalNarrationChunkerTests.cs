using CloudScribe.App.Composition;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class LocalNarrationChunkerTests
{
    [Theory]
    [InlineData("A paragraph with quotes \" and & markup <tag>.\n", 500)]
    [InlineData("Chinese 中文 with emoji 😀 and a paragraph.\n", 500)]
    [InlineData("A", 12000)]
    public void PartsPreserveExactTextAndEveryPartCompilesWithinCurrentLimit(string unit, int count)
    {
        string original = string.Concat(Enumerable.Repeat(unit, count));
        IReadOnlyList<string> parts = LocalNarrationChunker.Split(original, "en-US", "en-US-Standard-C");
        Assert.True(parts.Count > 1);
        Assert.Equal(original, string.Concat(parts));
        foreach (string part in parts)
        {
            Assert.False(char.IsLowSurrogate(part[0]));
            Assert.False(char.IsHighSurrogate(part[^1]));
            var plan = new SpeechPlan("en-US", [new SpeechText(part)], "test-local-parts");
            Assert.InRange(GoogleSpeechPlanCompiler.Compile(plan, new("en-US", "en-US-Standard-C", "MP3", 5000)).Payload.Length, 1, 5000);
        }
    }
}

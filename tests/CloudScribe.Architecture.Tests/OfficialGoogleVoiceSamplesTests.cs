using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class OfficialGoogleVoiceSamplesTests
{
    [Theory]
    [InlineData("en-US-Standard-C")]
    [InlineData("en-US-Neural2-A")]
    [InlineData("en-US-Chirp3-HD-Achernar")]
    public void CheckedVoiceUsesOnlyItsPublicPrerecordedResource(string voice)
    {
        Assert.Equal("https://docs.cloud.google.com/static/text-to-speech/docs/audio/" + voice + ".wav",
            OfficialGoogleVoiceSamples.SampleUrlFor(voice));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fr-FR-Unverified")]
    [InlineData("en-US-Standard-C?text=private")]
    [InlineData("../../private")]
    public void UnverifiedNamesNeverProduceAGuessedSampleLink(string? voice)
    {
        Assert.Null(OfficialGoogleVoiceSamples.SampleUrlFor(voice));
    }
}

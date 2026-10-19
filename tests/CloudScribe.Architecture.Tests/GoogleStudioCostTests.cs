using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleStudioCostTests
{
    [Theory]
    [InlineData("en-US-Standard-C", 2500, 1)]
    [InlineData("en-US-Standard-C", 2501, 2)]
    [InlineData("en-US-Neural2-C", 2500, 4)]
    [InlineData("en-US-Studio-O", 2500, 40)]
    public void AuthenticatedRateRoundsSubtotalUpwards(string voice, int characters, long expectedMinor)
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = new string('a', characters) } });
        var result = GoogleTtsStudioCostEstimator.Estimate(controls, voice, payload, "global");
        Assert.Equal(expectedMinor, result.MaximumMinorUnits);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void CompiledSsmlAndUnicodeAreConservativelyCounted()
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { ssml = "<speak>你好</speak>" } });
        var result = GoogleTtsStudioCostEstimator.Estimate(controls, "en-US-Standard-C", payload, "global");
        Assert.Equal(21, result.InputUtf8Bytes);
        Assert.Equal(1, result.MaximumMinorUnits);
    }

    [Theory]
    [InlineData("en-US-Unknown-A", "global", "test")]
    [InlineData("en-US-Polyglot-A", "global", "test")]
    [InlineData("en-US-Standard-C", "unadmitted-region", "test")]
    [InlineData("en-US-Standard-C", "global", "")]
    public void MissingAdmittedRateOrEmptyInputFailsClosed(string voice, string region, string input)
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = input } });
        Assert.Throws<InvalidOperationException>(() => GoogleTtsStudioCostEstimator.Estimate(controls, voice, payload, region));
    }
}

using System.Text;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationResponseParserTests
{
    [Fact]
    public void ParsesAudioAndOptionalOperationIdentity()
    {
        var payload = Encoding.UTF8.GetBytes("{\"audioContent\":\"AQIDBA==\",\"operationId\":\"op-17\"}");

        var parsed = GoogleGenerationResponseParser.Parse(payload, maximumAudioBytes: 16);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed.AudioBytes.ToArray());
        Assert.Equal("op-17", parsed.ProviderOperationId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"audioContent\":\"\"}")]
    [InlineData("{\"audioContent\":\"not-base64\"}")]
    public void InvalidMediaResponseFailsClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => GoogleGenerationResponseParser.Parse(Encoding.UTF8.GetBytes(json), 1024));
    }

    [Fact]
    public void DecodedMediaBoundIsEnforcedBeforeAcceptance()
    {
        var encoded = Convert.ToBase64String(new byte[33]);
        var payload = Encoding.UTF8.GetBytes($"{{\"audioContent\":\"{encoded}\"}}");

        Assert.Throws<InvalidDataException>(() => GoogleGenerationResponseParser.Parse(payload, maximumAudioBytes: 32));
    }
}

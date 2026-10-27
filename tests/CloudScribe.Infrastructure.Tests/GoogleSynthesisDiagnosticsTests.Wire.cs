using System.Net;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Fact]
    public async Task PostedWireContentIsExactlyCompiledPayloadAndCanonicalDetailsAreRedacted()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new { text = "Synthetic Chinese \u4e2d\u6587 \U0001f600 PRIVATE_REQUEST_SENTINEL" },
            voice = new { languageCode = "cmn-CN", name = "cmn-CN-Chirp3-HD-Alnilam" },
            audioConfig = new { audioEncoding = "MP3" },
        });
        using var http = new HttpClient(new ExactWireHandler(payload));
        var logger = new RecordingLogger();
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        var response = await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "synthetic-credential-reference", payload), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string log = string.Join('\n', logger.Messages);
        Assert.Contains("InputText", log, StringComparison.Ordinal);
        Assert.Contains("ServiceDisabled", log, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-MARKER", log, StringComparison.Ordinal);
    }

    private sealed class ExactWireHandler(byte[] expected) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(expected, await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            return new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""
                    {"error":{"status":"INVALID_ARGUMENT","message":"PRIVATE_RESPONSE_SENTINEL","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED","domain":"googleapis.com","metadata":{"credential":"PRIVATE_METADATA_SENTINEL"}},{"@type":"type.googleapis.com/google.rpc.BadRequest","fieldViolations":[{"field":"input.text","description":"PRIVATE_DESCRIPTION_SENTINEL"}]}]}}
                    """),
            };
        }
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Theory]
    [InlineData("absent", GoogleProviderRequestIdState.Absent)]
    [InlineData("multiple", GoogleProviderRequestIdState.Multiple)]
    [InlineData("oversized", GoogleProviderRequestIdState.Oversized)]
    public async Task RequestIdBoundsAndMissingnessNeverLogRawValues(string kind, GoogleProviderRequestIdState expected)
    {
        var logger = new RecordingLogger();
        using var handler = new ObserverFailureHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "PRIVATE_COOKIE=PRIVATE_COOKIE_VALUE");
            if (string.Equals(kind, "multiple", StringComparison.Ordinal))
            {
                response.Headers.TryAddWithoutValidation("x-request-id", "PRIVATE_ONE");
                response.Headers.TryAddWithoutValidation("x-request-id", "PRIVATE_TWO");
            }
            if (string.Equals(kind, "oversized", StringComparison.Ordinal)) response.Headers.TryAddWithoutValidation("x-request-id", new string('x', 257));
            return response;
        });
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "private-reference", "input"u8.ToArray()), TestContext.Current.CancellationToken);
        Assert.Equal(expected, Assert.Single(logger.Entries, x => x.EventId == 1610).Fields["RequestIdState"]);
        string logs = string.Join('\n', logger.Messages);
        Assert.DoesNotContain("PRIVATE_", logs, StringComparison.Ordinal); Assert.DoesNotContain(new string('x', 257), logs, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RequestIdIsHashedAndMissingExplanationCaptureIsExplicit()
    {
        const string privateId = "SYNTHETIC_PRIVATE_REQUEST_ID";
        var logger = new RecordingLogger();
        using var handler = new ObserverFailureHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"PRIVATE_PROVIDER_WORDING\"}}") };
            response.Headers.Add("x-goog-request-id", privateId); return response;
        });
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        var response = await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "private-reference", "private-input"u8.ToArray()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(1, handler.Calls);
        var id = Assert.Single(logger.Entries, x => x.EventId == 1610);
        Assert.Equal(GoogleProviderRequestIdState.PresentHashed, id.Fields["RequestIdState"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(privateId))), id.Fields["RequestIdSha256"]);
        var retention = Assert.Single(logger.Entries, x => x.EventId == 1608);
        Assert.Equal(GoogleExplanationRetentionState.NoActivePrivateTicket, retention.Fields["Retention"]);
        Assert.Contains(logger.Entries, x => x.EventId == 1611);
        string logs = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(privateId, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_PROVIDER_WORDING", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-reference", logs, StringComparison.Ordinal);
    }
}

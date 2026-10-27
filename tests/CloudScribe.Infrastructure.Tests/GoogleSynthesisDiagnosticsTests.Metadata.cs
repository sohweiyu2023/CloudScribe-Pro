using System.Net;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"audioContent\":\"U1lOVEhFVElD\"}", false)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"PRIVATE_RESPONSE_ECHO\"}}", true)]
    [InlineData(HttpStatusCode.BadRequest, "not-json", true)]
    [InlineData(HttpStatusCode.BadRequest, "", true)]
    public async Task LogsTypedResponseMetadataOnSuccessAndMalformedErrorsWithoutEcho(HttpStatusCode status, string body, bool hasErrorParse)
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new MetadataHandler(status, body));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        var response = await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        Assert.Equal(status, response.StatusCode);
        var metadata = Assert.Single(logger.Entries, x => x.EventId == 1606);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), metadata.Fields["BodyBytes"]);
        Assert.Equal(GoogleResponseReadState.Complete, metadata.Fields["ReadState"]);
        Assert.Equal(GoogleResponseMediaType.Json, metadata.Fields["MediaType"]);
        Assert.Equal(GoogleResponseLengthState.Matches, metadata.Fields["LengthState"]);
        Assert.Equal(hasErrorParse, logger.Entries.Any(x => x.EventId == 1607));
        AssertRedacted(logger);
    }

    [Fact]
    public async Task SnapshotBeforeCredentialAwaitKeepsSentContentAndLoggedHashCoherent()
    {
        byte[] payload = Encoding.UTF8.GetBytes("Synthetic \u4e2d\u6587 \U0001f600 PRIVATE_REQUEST");
        byte[] expected = payload.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(expected));
        var logger = new RecordingLogger();
        using var handler = new SnapshotHandler(expected);
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new MutatingCredential(payload), new Uri("https://texttospeech.googleapis.com"), logger);
        await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", payload), TestContext.Current.CancellationToken);
        var wire = Assert.Single(logger.Entries, x => x.EventId == 1605);
        Assert.Equal(hash, wire.Fields["PayloadSha256"]);
        Assert.Equal(expected.Length, wire.Fields["PayloadBytes"]);
        Assert.Equal(GoogleSynthesisEndpointIdentity.GlobalV1Synthesize, wire.Fields["Endpoint"]);
        AssertRedacted(logger);
    }

    [Theory]
    [InlineData("https://us-texttospeech.googleapis.com/v1/text:synthesize", GoogleSynthesisEndpointIdentity.RegionalV1Synthesize, false)]
    [InlineData("https://texttospeech.googleapis.com/v1/text:synthesize?PRIVATE_QUERY=PRIVATE_TOKEN", GoogleSynthesisEndpointIdentity.OtherPinnedEndpoint, true)]
    public async Task EndpointIdentityContainsOnlyPinnedOriginHashAndKnownRoute(string endpoint, GoogleSynthesisEndpointIdentity expected, bool hasQuery)
    {
        var uri = new Uri(endpoint);
        var logger = new RecordingLogger();
        using var http = new HttpClient(new MetadataHandler(HttpStatusCode.BadRequest, "{}"));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri(uri.GetLeftPart(UriPartial.Authority)), logger);
        await transport.SendAsync(new(uri, "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        var wire = Assert.Single(logger.Entries, x => x.EventId == 1605);
        Assert.Equal(expected, wire.Fields["Endpoint"]);
        Assert.Equal(hasQuery, wire.Fields["HasQuery"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.GetLeftPart(UriPartial.Authority)))), wire.Fields["OriginSha256"]);
        AssertRedacted(logger);
    }

    [Fact]
    public async Task NewDiagnosticSinkFailureDoesNotChangeProviderResponse()
    {
        using var http = new HttpClient(new MetadataHandler(HttpStatusCode.BadRequest, "{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}"));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), new ThrowingDiagnosticLogger());
        var response = await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(GoogleErrorStatus.InvalidArgument, response.ErrorStatus);
    }

    private static void AssertRedacted(RecordingLogger logger)
    {
        string text = string.Join('\n', logger.Messages.Concat(logger.Entries.SelectMany(x => x.Fields.Values).Select(x => x?.ToString())));
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", text, StringComparison.Ordinal);
        Assert.All(logger.Entries, x => Assert.False(x.HasException));
    }

    private sealed class MetadataHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            content.Headers.ContentLength = Encoding.UTF8.GetByteCount(body);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class MutatingCredential(byte[] payload) : ITransientCredentialResolver
    {
        public async ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        { await Task.Delay(1, cancellationToken).ConfigureAwait(false); payload[0] = (byte)'X'; return "PRIVATE_TOKEN"; }
    }

    private sealed class SnapshotHandler(byte[] expected) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(expected, await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}") };
        }
    }

    private sealed class ThrowingDiagnosticLogger : ILogger<GoogleGenerationHttpTransport>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (eventId.Id is 1605 or 1606 or 1607) throw new InvalidOperationException("PRIVATE_OBSERVER_FAILURE"); }
    }
}

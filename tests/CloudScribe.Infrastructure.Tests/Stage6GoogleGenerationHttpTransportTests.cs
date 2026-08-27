using System.Net;
using System.Net.Http.Headers;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationHttpTransportTests
{
    [Fact]
    public async Task SendAsync_UsesTransientCredentialAndPreservesRetryAfter()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        using var client = new HttpClient(handler);
        var resolver = new FixedCredentialResolver("token-value");
        var transport = new GoogleGenerationHttpTransport(client, resolver, new Uri("https://texttospeech.googleapis.com/"));

        var result = await transport.SendAsync(
            new GoogleHttpTransportRequest(
                new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
                "cred-ref-1",
                new byte[] { 9 }),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal((HttpStatusCode)429, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("token-value", handler.Authorization?.Parameter);
        Assert.Equal("cred-ref-1", resolver.LastReference);
    }

    [Fact]
    public async Task SendAsync_RejectsOriginDriftBeforeCredentialResolution()
    {
        var resolver = new FixedCredentialResolver("token");
        using var client = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var transport = new GoogleGenerationHttpTransport(client, resolver, new Uri("https://texttospeech.googleapis.com/"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(
            new GoogleHttpTransportRequest(
                new Uri("https://evil.example/v1/text:synthesize"),
                "cred",
                new byte[] { 1 }),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Null(resolver.LastReference);
    }

    [Fact]
    public async Task SendAsync_RejectsOversizedResponse()
    {
        using var client = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[32])
        }));
        var transport = new GoogleGenerationHttpTransport(client, new FixedCredentialResolver("token"), new Uri("https://texttospeech.googleapis.com/"));

        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync(
            new GoogleHttpTransportRequest(
                new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
                "cred",
                new byte[] { 1 },
                MaximumResponseBytes: 8),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    private sealed class FixedCredentialResolver(string token) : ITransientCredentialResolver
    {
        public string? LastReference { get; private set; }
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        {
            LastReference = credentialReferenceId;
            return ValueTask.FromResult(token);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(factory(request));
        }
    }
}

using System.Net;
using System.Net.Http;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleGenerationProviderTests
{
    [Fact]
    public async Task SubmitAsync_MapsValidGoogleResponseToAcceptedMedia()
    {
        var audio = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"audioContent\":\"{audio}\",\"operationId\":\"op-1\"}}"),
        }));
        var account = new GoogleGenerationAccount("acct", "cred-ref", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global");
        var transport = new GoogleGenerationHttpTransport(client, new StubCredentialResolver(), new Uri("https://texttospeech.googleapis.com"));
        var provider = new GoogleGenerationProvider(account, transport);
        var request = new GenerationProviderRequest(
            GoogleGenerationProvider.StableProviderId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "acct",
            "idem-1",
            new byte[] { 1 },
            "mp3");

        var response = await provider.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Equal("op-1", response.ProviderRequestId);
        Assert.Equal(new byte[] { 1, 2, 3 }, response.MediaBytes.ToArray());
        Assert.Equal("audio/mpeg", response.MediaContentType);
    }

    [Fact]
    public async Task SubmitAsync_RejectsNonCanonicalOperationBeforeHttp()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var account = new GoogleGenerationAccount("acct", "cred-ref", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global");
        var transport = new GoogleGenerationHttpTransport(client, new StubCredentialResolver(), new Uri("https://texttospeech.googleapis.com"));
        var provider = new GoogleGenerationProvider(account, transport);
        var request = new GenerationProviderRequest(
            GoogleGenerationProvider.StableProviderId,
            "synthesize",
            "acct",
            "idem-drift",
            new byte[] { 1 },
            "mp3");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitAsync(request, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotInventSafeRetryEvidence()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var account = new GoogleGenerationAccount("acct", "cred-ref", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global");
        var transport = new GoogleGenerationHttpTransport(client, new StubCredentialResolver(), new Uri("https://texttospeech.googleapis.com"));
        var provider = new GoogleGenerationProvider(account, transport);

        var result = await provider.ReconcileAsync("idem-ambiguous", CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class StubCredentialResolver : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
            => ValueTask.FromResult("transient-token");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}

using System.Net;
using System.Net.Http;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage6GoogleAuthorizedGenerationExecutorTests
{
    [Fact]
    public async Task SubmitAsync_ExactAuthorizedIdentityReachesProvider()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"audioContent\":\"AQID\"}"),
            };
        }));
        var setup = CreateSetup(client, new byte[] { 1, 2, 3 });

        var response = await setup.Executor.SubmitAsync(setup.Request);

        Assert.Equal(1, calls);
        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
    }

    [Fact]
    public async Task SubmitAsync_PayloadDriftFailsBeforeCredentialOrHttpUse()
    {
        var calls = 0;
        var resolver = new CountingCredentialResolver();
        using var client = ClientCountingCalls(() => calls++);
        var setup = CreateSetup(client, new byte[] { 1, 2, 3 }, resolver);
        var drifted = RequestLike(setup.Request, payload: new byte[] { 9, 9, 9 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Executor.SubmitAsync(drifted));

        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SubmitAsync_OperationDriftFailsBeforeCredentialOrHttpUse()
    {
        var calls = 0;
        var resolver = new CountingCredentialResolver();
        using var client = ClientCountingCalls(() => calls++);
        var setup = CreateSetup(client, new byte[] { 1, 2, 3 }, resolver);
        var drifted = RequestLike(setup.Request, operation: "list-voices");

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Executor.SubmitAsync(drifted));

        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SubmitAsync_OutputFormatDriftFailsBeforeCredentialOrHttpUse()
    {
        var calls = 0;
        var resolver = new CountingCredentialResolver();
        using var client = ClientCountingCalls(() => calls++);
        var setup = CreateSetup(client, new byte[] { 1, 2, 3 }, resolver);
        var drifted = RequestLike(setup.Request, outputFormat: "wav");

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Executor.SubmitAsync(drifted));

        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, calls);
    }

    private static HttpClient ClientCountingCalls(Action onCall) => new(new StubHandler(_ =>
    {
        onCall();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"audioContent\":\"AQID\"}"),
        };
    }));

    private static GenerationProviderRequest RequestLike(
        GenerationProviderRequest source,
        string? operation = null,
        ReadOnlyMemory<byte>? payload = null,
        string? outputFormat = null) => new(
            source.ProviderStableId,
            operation ?? source.OperationStableId,
            source.AccountId,
            source.IdempotencyKey,
            payload ?? source.CompiledPayload,
            outputFormat ?? source.OutputFormat);

    private static Setup CreateSetup(
        HttpClient client,
        byte[] payload,
        ITransientCredentialResolver? resolver = null)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new GoogleGenerationAccount(
            "acct",
            "credential-ref",
            new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "global");
        var capabilities = new GoogleCapabilitySnapshot(
            "acct",
            "cap-prov",
            now.AddMinutes(-1),
            now.AddHours(1),
            new HashSet<string>(StringComparer.Ordinal) { "en-US-Test" },
            new HashSet<string>(StringComparer.Ordinal) { "MP3" },
            4096);
        var envelope = GoogleGenerationSubmissionEnvelope.Create(
            account,
            capabilities,
            "pricing-prov",
            7,
            "en-US-Test",
            "MP3",
            payload,
            now);
        var authorization = GoogleGenerationSpendAuthorization.Create(
            envelope,
            "USD",
            6,
            125,
            200);
        var transport = new GoogleGenerationHttpTransport(
            client,
            resolver ?? new CountingCredentialResolver(),
            new Uri("https://texttospeech.googleapis.com"));
        var provider = new GoogleGenerationProvider(account, transport);
        var executor = new GoogleAuthorizedGenerationExecutor(
            provider,
            account,
            capabilities,
            authorization,
            "pricing-prov",
            7,
            "USD",
            6,
            125);
        var request = new GenerationProviderRequest(
            GoogleGenerationProvider.StableProviderId,
            "synthesize",
            "acct",
            "idem-1",
            payload,
            "mp3");
        return new Setup(executor, request);
    }

    private sealed record Setup(
        GoogleAuthorizedGenerationExecutor Executor,
        GenerationProviderRequest Request);

    private sealed class CountingCredentialResolver : ITransientCredentialResolver
    {
        public int ResolveCalls { get; private set; }

        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return ValueTask.FromResult("transient-token");
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}

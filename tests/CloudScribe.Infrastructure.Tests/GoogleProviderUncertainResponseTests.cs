using System.Net;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleProviderUncertainResponseTests
{
    [Theory]
    [InlineData(400, "INVALID_ARGUMENT", GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData(503, "INVALID_ARGUMENT", GoogleValidationMessageHint.NotExamined)]
    [InlineData(400, "UNAVAILABLE", GoogleValidationMessageHint.NotExamined)]
    public async Task SafeValidationHintSurvivesTransportWithoutChangingFinancialDisposition(int status, string errorStatus, GoogleValidationMessageHint hint)
    {
        string body = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = new { status = errorStatus, message = "This request contains sentences that are too long." },
        });
        using var http = new HttpClient(new ResponseHandler((HttpStatusCode)status, body));
        var response = await Create(http).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(hint, response.ValidationHint);
        Assert.Equal(status == 400 ? SubmissionDisposition.NotSubmitted : SubmissionDisposition.UnknownRequiresReconciliation, response.Disposition);
        Assert.Null(response.ProviderRequestId);
        Assert.True(response.MediaBytes.IsEmpty);
        Assert.DoesNotContain("This request", System.Text.Json.JsonSerializer.Serialize(response), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RetryAfterNeverProvesSynchronousSubmissionSafeToRepeat(int status)
    {
        using var http = new HttpClient(new ResponseHandler((HttpStatusCode)status, "{\"error\":{\"message\":\"synthetic secret\"}}"));
        var provider = Create(http);
        var response = await provider.SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(SubmissionDisposition.UnknownRequiresReconciliation, response.Disposition);
        Assert.Equal($"google-http-{status}", response.DiagnosticCode);
        Assert.Null(response.ProviderRequestId);
        Assert.Equal(GoogleRetryDisposition.ReconcileBeforeRetry, GoogleProviderResponsePolicy.Classify(status, TimeSpan.FromSeconds(1), false).Disposition);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    public async Task MalformedAcceptedBodyRetainsAcceptanceWithoutInventedProviderIdentity(string body)
    {
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, body));
        var response = await Create(http).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Equal("google-accepted-invalid-media", response.DiagnosticCode);
        Assert.Null(response.ProviderRequestId);
        Assert.True(response.MediaBytes.IsEmpty);
    }

    [Fact]
    public async Task ValidSynchronousResponseDoesNotInventProviderId()
    {
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, "{\"audioContent\":\"AQID\"}"));
        var response = await Create(http).SubmitAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(response.IsAccepted);
        Assert.Equal(new byte[] { 1, 2, 3 }, response.MediaBytes.ToArray());
        Assert.Null(response.ProviderRequestId);
    }

    private static GoogleGenerationProvider Create(HttpClient http) => new(
        new GoogleGenerationAccount("synthetic-account", "synthetic-reference", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"),
        new GoogleGenerationHttpTransport(http, new SyntheticCredential(), new Uri("https://texttospeech.googleapis.com")));

    private static GenerationProviderRequest Request() => new(GoogleGenerationProvider.StableProviderId,
        GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-account", "synthetic-key", new byte[] { 1 }, "mp3");

    private sealed class SyntheticCredential : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) => ValueTask.FromResult("synthetic-not-a-real-token");
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        }
    }
}

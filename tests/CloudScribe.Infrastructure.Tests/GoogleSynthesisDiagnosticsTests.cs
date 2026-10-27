using System.Net;
using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Theory]
    [InlineData("{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"secret\",\"details\":[\"private\"]}}", GoogleErrorStatus.PermissionDenied)]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}", GoogleErrorStatus.InvalidArgument)]
    [InlineData("{\"error\":{\"status\":\"PERMISSION_DENIED\",\"status\":\"INTERNAL\"}}", GoogleErrorStatus.Unknown)]
    [InlineData("{\"error\":{\"status\":\"PERMISSION_DENIED secret\"}}", GoogleErrorStatus.Unknown)]
    [InlineData("{\"error\":{\"message\":\"private\"}}", GoogleErrorStatus.NotProvided)]
    [InlineData("not-json", GoogleErrorStatus.Unknown)]
    public void ParsesOnlyCanonicalStatus(string body, GoogleErrorStatus expected) =>
        Assert.Equal(expected, GoogleErrorStatusParser.Parse(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void OversizedBodyNeverEntersErrorParser() =>
        Assert.Equal(GoogleErrorStatus.Unknown, GoogleErrorStatusParser.Parse(new byte[16 * 1024 + 1]));

    [Fact]
    public async Task LogsHeadersBeforeBodyAndNeverLogsCredentialsTextOrProviderMessages()
    {
        const string marker = "SYNTHETIC-PRIVATE-MARKER";
        var logger = new RecordingLogger();
        using var http = new HttpClient(new SyntheticHandler());
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        var response = await transport.SendAsync(new GoogleHttpTransportRequest(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "synthetic-credential-reference", Encoding.UTF8.GetBytes(marker)), TestContext.Current.CancellationToken);
        Assert.Equal(GoogleErrorStatus.PermissionDenied, response.ErrorStatus);
        string logged = string.Join('\n', logger.Messages);
        Assert.Contains("ResponseHeaders", logged, StringComparison.Ordinal);
        Assert.Contains("403", logged, StringComparison.Ordinal);
        Assert.Contains("PermissionDenied", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-credential-reference", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", logged, StringComparison.Ordinal);
        Assert.True(logger.Messages.FindIndex(message => message.Contains("ResponseHeaders", StringComparison.Ordinal))
            < logger.Messages.FindIndex(message => message.Contains("ResponseReadCompleted", StringComparison.Ordinal)));
    }

    private sealed class Credential : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult("SYNTHETIC-PRIVATE-MARKER");
    }
    private sealed class SyntheticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"SYNTHETIC-PRIVATE-MARKER\"}}") });
    }
    private sealed class RecordingLogger : ILogger<GoogleGenerationHttpTransport>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

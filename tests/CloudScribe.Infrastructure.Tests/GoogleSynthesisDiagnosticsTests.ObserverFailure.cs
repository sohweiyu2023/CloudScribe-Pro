using System.Net;
using System.Text;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(400, false)]
    [InlineData(503, false)]
    [InlineData(200, true)]
    [InlineData(400, true)]
    public async Task LoggerFailureCannotPreventDispatchOrReplaceCompletedResponse(int status, bool failEnabled)
    {
        byte[] body = "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"SYNTHETIC_PRIVATE\"}}"u8.ToArray();
        using var handler = new ObserverFailureHandler(() => new HttpResponseMessage((HttpStatusCode)status)
        { Content = new ByteArrayContent(body) });
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), new FailingObserver(failEnabled));
        long before = CloudScribeLog.ObserverFailureCount;
        var response = await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "synthetic-reference", Encoding.UTF8.GetBytes("SYNTHETIC_PRIVATE_INPUT")), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(body, response.Body.ToArray());
        Assert.True(CloudScribeLog.ObserverFailureCount > before);
    }

    [Fact]
    public async Task LoggerFailureCannotReplaceOriginalTransportExceptionOrCreateRetry()
    {
        var expected = new HttpRequestException("SYNTHETIC_PRIVATE_FAILURE");
        using var handler = new ObserverFailureHandler(() => throw expected);
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), new FailingObserver(false));
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(new(
            new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "synthetic-reference", "synthetic"u8.ToArray()), TestContext.Current.CancellationToken));
        Assert.Same(expected, actual);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class FailingObserver(bool failEnabled) : ILogger<GoogleGenerationHttpTransport>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => failEnabled ? throw new InvalidOperationException("SYNTHETIC_PRIVATE_LOGGER") : true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("SYNTHETIC_PRIVATE_LOGGER");
    }

    private sealed class ObserverFailureHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response()); }
    }
}

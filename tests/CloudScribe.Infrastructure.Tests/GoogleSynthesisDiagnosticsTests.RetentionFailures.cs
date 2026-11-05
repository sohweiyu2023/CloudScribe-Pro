using System.Net;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Fact]
    public async Task MissingHeadersReportsCaptureUnavailableWithoutLosingTransportFailure()
    {
        var logger = new RecordingLogger();
        using var handler = new ObserverFailureHandler(() => throw new HttpRequestException("PRIVATE_PROVIDER_WORDING"));
        using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "PRIVATE_REFERENCE", "PRIVATE_INPUT"u8.ToArray()), TestContext.Current.CancellationToken));
        var evidence = Assert.Single(logger.Entries, x => x.EventId == 1608);
        Assert.Equal(0, evidence.Fields["BodyBytes"]);
        Assert.Equal(0, evidence.Fields["HttpStatus"]);
        Assert.Equal(GoogleExplanationRetentionState.NoActivePrivateTicket, evidence.Fields["Retention"]);
        Assert.DoesNotContain("PRIVATE_", string.Join('\n', logger.Messages), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialResponseReportsRetentionOnceAndPreservesOriginalFailure(bool cancel)
    {
        var logger = new RecordingLogger();
        using var content = new StreamContent(new InterruptingStream(cancel));
        using var http = new HttpClient(new ContentHandler(content));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        var request = new GoogleHttpTransportRequest(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "PRIVATE_REFERENCE", "PRIVATE_INPUT"u8.ToArray());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(request, TestContext.Current.CancellationToken));
        var evidence = Assert.Single(logger.Entries, x => x.EventId == 1608);
        Assert.Equal(3, evidence.Fields["BodyBytes"]);
        Assert.Equal((int)HttpStatusCode.BadRequest, evidence.Fields["HttpStatus"]);
        Assert.Equal(GoogleExplanationRetentionState.NoActivePrivateTicket, evidence.Fields["Retention"]);
        Assert.DoesNotContain("PRIVATE_", string.Join('\n', logger.Messages), StringComparison.Ordinal);
    }
}

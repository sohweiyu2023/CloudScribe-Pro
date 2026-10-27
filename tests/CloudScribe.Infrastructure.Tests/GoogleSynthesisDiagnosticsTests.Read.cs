using System.Net;
using System.Text;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleSynthesisDiagnosticsTests
{
    [Theory]
    [InlineData(false, GoogleResponseReadState.Failed)]
    [InlineData(true, GoogleResponseReadState.Cancelled)]
    public async Task InterruptedReadRetainsByteCountAndNeverClaimsCompletedError(bool cancel, GoogleResponseReadState expected)
    {
        var logger = new RecordingLogger();
        using var content = new StreamContent(new InterruptingStream(cancel));
        content.Headers.ContentLength = 100;
        using var http = new HttpClient(new ContentHandler(content));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        Task<GoogleHttpTransportResponse> action = transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        else await Assert.ThrowsAsync<IOException>(() => action);
        var metadata = Assert.Single(logger.Entries, x => x.EventId == 1606);
        Assert.Equal(expected, metadata.Fields["ReadState"]);
        Assert.Equal(3, metadata.Fields["BodyBytes"]);
        Assert.Equal(GoogleResponseLengthState.DeclaredButReadIncomplete, metadata.Fields["LengthState"]);
        Assert.DoesNotContain(logger.Entries, x => x.EventId == 1607);
        Assert.DoesNotContain(logger.Messages, x => x.Contains("ResponseReadCompleted", StringComparison.Ordinal));
        AssertRedacted(logger);
    }

    [Fact]
    public async Task OverflowRecordsConsumedBytesAndPreservesOriginalExceptionPath()
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new MetadataHandler(HttpStatusCode.BadRequest, "12345678"));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray(), 4), TestContext.Current.CancellationToken));
        var metadata = Assert.Single(logger.Entries, x => x.EventId == 1606);
        Assert.Equal(GoogleResponseReadState.Oversize, metadata.Fields["ReadState"]);
        Assert.Equal(8, metadata.Fields["BodyBytes"]);
        Assert.DoesNotContain(logger.Entries, x => x.EventId == 1607);
        AssertRedacted(logger);
    }

    [Theory]
    [InlineData(false, GoogleResponseLengthState.Absent)]
    [InlineData(true, GoogleResponseLengthState.Mismatch)]
    public async Task ComputableContentDoesNotInventDeclaredLengthAndMismatchIsObservable(bool setLength, GoogleResponseLengthState expected)
    {
        var logger = new RecordingLogger();
        using var content = new StringContent("{}", Encoding.UTF8, "text/html");
        Assert.False(content.Headers.Contains("Content-Length"));
        if (setLength) content.Headers.ContentLength = 5;
        using var http = new HttpClient(new ContentHandler(content));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        var metadata = Assert.Single(logger.Entries, x => x.EventId == 1606);
        Assert.Equal(expected, metadata.Fields["LengthState"]);
        Assert.Equal(GoogleResponseMediaType.Other, metadata.Fields["MediaType"]);
        AssertRedacted(logger);
    }

    [Theory]
    [InlineData(false, GoogleResponseMediaType.Absent)]
    [InlineData(true, GoogleResponseMediaType.Invalid)]
    public async Task MissingAndMalformedMediaTypeAreCategorizedWithoutHeaderEcho(bool malformed, GoogleResponseMediaType expected)
    {
        var logger = new RecordingLogger();
        using var content = new ByteArrayContent("{}"u8.ToArray());
        if (malformed) Assert.True(content.Headers.TryAddWithoutValidation("Content-Type", "PRIVATE_INVALID_HEADER"));
        using var http = new HttpClient(new ContentHandler(content));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        var metadata = Assert.Single(logger.Entries, x => x.EventId == 1606);
        Assert.Equal(expected, metadata.Fields["MediaType"]);
        AssertRedacted(logger);
    }

    [Fact]
    public async Task InvalidUnicodeCanonicalStatusCannotEscapeAndOversizedDiagnosticBodyIsObservable()
    {
        foreach (string body in new[] { "{\"error\":{\"status\":\"\\uD800\"}}", new string('x', 16385) })
        {
            var logger = new RecordingLogger();
            using var http = new HttpClient(new MetadataHandler(HttpStatusCode.BadRequest, body));
            var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
            var response = await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
            Assert.Equal(GoogleErrorStatus.Unknown, response.ErrorStatus);
            var parse = Assert.Single(logger.Entries, x => x.EventId == 1607);
            Assert.Equal(body.Length > 16384 ? GoogleErrorBodyParseState.OverDiagnosticBound : GoogleErrorBodyParseState.InvalidJson, parse.Fields["ParseState"]);
            AssertRedacted(logger);
        }
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-2")]
    [InlineData("9223372036854775808")]
    [InlineData("2, 2")]
    public async Task MalformedDeclaredLengthCannotBeReplacedByComputedLength(string value)
    {
        var logger = new RecordingLogger();
        using var content = new StringContent("{}");
        Assert.True(content.Headers.TryAddWithoutValidation("Content-Length", value));
        using var http = new HttpClient(new ContentHandler(content));
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger);
        await transport.SendAsync(new(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "PRIVATE_REFERENCE", "PRIVATE_REQUEST"u8.ToArray()), TestContext.Current.CancellationToken);
        var metadata = Assert.Single(logger.Entries, x => x.EventId == 1606);
        Assert.Equal(GoogleResponseLengthState.Invalid, metadata.Fields["LengthState"]);
        AssertRedacted(logger);
    }

    private sealed class ContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content });
    }

    private sealed class InterruptingStream(bool cancel) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read) { _read = true; "123"u8.CopyTo(buffer.Span); return ValueTask.FromResult(3); }
            return ValueTask.FromException<int>(cancel ? new OperationCanceledException("PRIVATE_ERROR") : new IOException("PRIVATE_ERROR"));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

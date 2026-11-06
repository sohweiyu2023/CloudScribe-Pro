using System.Net;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongAudioMetadataIsCapturedBeforeParsingAndNeverBecomesAudioOnMalformedOrOversizeBody(bool oversize)
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        var store = new ProtectedStore(); var observer = new GoogleLongAudioProtectedResponseObserver(store, new Clock());
        observer.ArmForJob(request, true);
        string body = "METADATA EXPLANATION synthetic-token synthetic article <&> \u4f60\u597d" + (oversize ? new string('x', 17000) : "");
        using var boundary = output.Boundary(f.Journal, (_, _) => Task.FromResult(Json(body)), observer);
        await Assert.ThrowsAnyAsync<Exception>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        var record = Assert.Single(store.Records); Assert.Equal(200, record.Response!.HttpStatus);
        Assert.Contains("METADATA EXPLANATION", record.Response.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", record.Response.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic article", record.Response.RedactedBody, StringComparison.Ordinal);
        Assert.Equal(0, output.Native.Calls);
        Assert.Equal(GoogleLongAudioState.CompletedUnverified, f.Journal.Load(request.AttemptId).State);
        Assert.Empty(Directory.EnumerateFiles(output.OutputRoot, "*.mp3"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongAudioExchangeEvidenceBindsAttemptAndHashesProviderIdentityWithoutRawResponse(bool networkFailure)
    {
        using var f = new Fixture(); var request = Request(); var log = new ExchangeLog(); int calls = 0;
        using var adapter = new GoogleLongAudioAdapter(new Credentials(), new Admission(), f.Journal, new ReadyObserver(),
            new Handler((_, _) =>
            {
                calls++; if (networkFailure) throw new HttpRequestException("PRIVATE exception prose");
                var response = Json(Operation(false)); response.Headers.Add("x-goog-request-id", "PRIVATE provider header");
                return Task.FromResult(response);
            }), log);
        if (networkFailure) await Assert.ThrowsAsync<HttpRequestException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        else _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        string text = string.Join('\n', log.Messages);
        Assert.Contains(request.AttemptId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("send started True", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic article", text, StringComparison.Ordinal);
        Assert.NotNull(f.Journal.Load(request.AttemptId).Observation);
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongAudioStoragePreservesOriginalFailureAndRecordsActualNoResponseOrPartialRead(bool partialBody)
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        var store = new ProtectedStore(); var observer = new GoogleLongAudioProtectedResponseObserver(store, new Clock());
        observer.ArmForJob(request, true); var original = new IOException("PRIVATE original storage failure"); var log = new ExchangeLog();
        using var boundary = output.Boundary(f.Journal, (_, _) => partialBody
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PartialStream(original)) })
            : throw original, observer, log);
        if (partialBody)
        {
            var error = await Assert.ThrowsAsync<GoogleLongAudioStorageReadException>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
            Assert.Same(original, error.InnerException); Assert.Equal(200, error.HttpStatus);
            Assert.Equal(GoogleResponseReadState.Failed, error.Observation.ReadState);
        }
        else Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken)));
        var record = Assert.Single(store.Records); Assert.Equal(partialBody ? 200 : 0, record.Response!.HttpStatus);
        string safe = string.Join('\n', log.Messages);
        Assert.Contains("StorageMetadataV1", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", safe, StringComparison.Ordinal); Assert.Equal(0, output.Native.Calls);
    }

    private sealed class ExchangeLog : ILogger
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

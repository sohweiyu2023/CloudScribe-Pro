using System.Net;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedObserverRetainsUnknownErrorAndRedactsEchoWithoutChangingActualHttpStatus(bool operationError)
    {
        using var fixture = new Fixture(); var request = Request(); var store = new ProtectedStore(); var clock = new Clock();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1));
        string message = "Unfamiliar provider explanation: synthetic-token and synthetic article <&> \u4f60\u597d";
        string body = operationError ? JsonSerializer.Serialize(new { name = Name, done = true, error = new { code = 3, message } })
            : JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message } });
        using var adapter = fixture.Adapter(new Admission(), (_, _) => Task.FromResult(new HttpResponseMessage(operationError ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
        { Content = new StringContent(body) }), observer);
        var result = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.Failed, result.State);
        Assert.Equal(operationError ? 200 : 400, result.HttpStatus);
        Assert.True(result.Observation!.ReadbackVerified); Assert.Equal(GoogleLongAudioParseState.Json, result.Observation.ParseState);
        var record = Assert.Single(store.Records); Assert.Equal(result.HttpStatus, record.Response!.HttpStatus);
        Assert.Contains("Unfamiliar provider explanation", record.Response.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", record.Response.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic article", record.Response.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Unfamiliar", fixture.JournalText(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", fixture.JournalText(), StringComparison.Ordinal);
        Assert.False(observer.IsReadyFor(request)); Assert.Equal(1, store.Preparations); Assert.Equal(1, store.Readbacks);
    }

    [Theory]
    [InlineData("save-false")]
    [InlineData("save-throw")]
    [InlineData("read-missing")]
    public async Task ProtectedStorageFailurePreserves400AndDurablyStopsTheNextPaidPost(string mode)
    {
        using var fixture = new Fixture(); var request = Request(); var store = new ProtectedStore { Mode = mode }; var clock = new Clock();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1)); int calls = 0; var admission = new Admission();
        using var adapter = fixture.Adapter(admission, (_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"Unknown protected wording\"}}") }); }, observer);
        var result = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(400, result.HttpStatus); Assert.Equal(GoogleLongAudioState.Failed, result.State);
        Assert.False(result.Observation!.ReadbackVerified); Assert.Equal(GoogleExplanationRetentionState.StorageFailed, result.Observation.Retention);
        Assert.Equal(result, fixture.Journal.Load(request.AttemptId));
        var next = Request(); observer.Arm(next, true, clock.GetUtcNow().AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(next, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.Equal(1, admission.Calls);
    }

    [Fact]
    public async Task ExactObjectConsentAndLargeInputFailClosedBeforeDurableClaimOrHttp()
    {
        using var fixture = new Fixture(); var clock = new Clock(); var request = Request(); var store = new ProtectedStore();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock); int calls = 0;
        using var adapter = fixture.Adapter(new Admission(), (_, _) => { calls++; return Task.FromResult(Json(Operation(false))); }, observer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => observer.Arm(request, false, clock.GetUtcNow().AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => observer.Arm(Request(text: new string('x', 20_000)), true, clock.GetUtcNow().AddHours(1)));
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1));
        Assert.False(observer.IsReadyFor(Request(request.AttemptId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(Request(request.AttemptId), TestContext.Current.CancellationToken));
        Assert.Equal(0, calls); Assert.Empty(fixture.JournalText());
    }

    [Fact]
    public void ExplicitCaptureExpiryIsNotANewPaidWindowAndCannotExtendThroughWallClockRollback()
    {
        var clock = new Clock(); var observer = new GoogleLongAudioProtectedResponseObserver(new ProtectedStore(), clock); var request = Request();
        Assert.Throws<InvalidOperationException>(() => observer.Arm(request, true, clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => observer.Arm(request, true, clock.GetUtcNow().AddHours(6).AddTicks(1)));
        observer.Arm(request, true, clock.GetUtcNow().AddHours(2)); Assert.True(observer.IsReadyFor(request));
        clock.AdvanceMonotonic(TimeSpan.FromHours(2)); clock.RollBackUtc(TimeSpan.FromHours(1));
        Assert.False(observer.IsReadyFor(request));
    }

    [Fact]
    public async Task InvalidOperationErrorFlagRetainsOnlyHonestMissingnessAndActual200()
    {
        var clock = new Clock(); var store = new ProtectedStore(); var request = Request();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1)); Guid ticket = observer.Begin(request);
        var result = await observer.ObserveAsync(ticket, request, 200, "{\"error\":{\"code\":3}}"u8.ToArray(),
            GoogleResponseReadState.Complete, "synthetic-token", operationError: true);
        Assert.True(result.ReadbackVerified); Assert.True(result.DetailsOmitted);
        Assert.Equal(GoogleLongAudioParseState.RedactionUnavailable, result.ParseState);
        var record = Assert.Single(store.Records); Assert.Equal(200, record.Response!.HttpStatus);
        Assert.Empty(record.Response.RedactedBody);
    }

    [Fact]
    public async Task ThrowingObservationAndCleanupCannotReplaceTheActual400OrPermitAnotherPost()
    {
        using var fixture = new Fixture(); int calls = 0;
        using var adapter = fixture.Adapter(new Admission(), (_, _) =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"message\":\"PRIVATE synthetic echo\"}}") });
        }, new ThrowingObserver());
        var result = await adapter.SubmitOnceAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(400, result.HttpStatus); Assert.Equal(GoogleLongAudioState.Failed, result.State);
        Assert.False(result.Observation!.ReadbackVerified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.DoesNotContain("PRIVATE", fixture.JournalText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedOperationIdentityStillCapturesItsActual200ErrorWithoutAcceptingTheOperation()
    {
        using var fixture = new Fixture(); var request = Request(); var store = new ProtectedStore(); var clock = new Clock();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1));
        using var adapter = fixture.Adapter(new Admission(), (_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        { name = "projects/999/locations/global/operations/wrong", done = true, error = new { code = 3, message = "Unfamiliar operation explanation" } }))), observer);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        var stored = fixture.Journal.Load(request.AttemptId);
        Assert.True(stored.OperationParseFailed); Assert.Equal(GoogleLongAudioState.PostClaimed, stored.State); Assert.Null(stored.OperationName);
        var record = Assert.Single(store.Records); Assert.Equal(200, record.Response!.HttpStatus);
        Assert.Contains("Unfamiliar operation explanation", record.Response.RedactedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JournalWriteFailureStillAttemptsProtectedReadbackAndNeverReplacesOrLogsTheOriginalFailure()
    {
        using var fixture = new Fixture(); var request = Request(); var store = new ProtectedStore(); var clock = new Clock();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock); string original = string.Empty; int calls = 0;
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1));
        using var adapter = fixture.Adapter(new Admission(), (_, _) =>
        {
            calls++; original = fixture.JournalText(); fixture.ReplaceJournalText(request.AttemptId, original, "{");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"Unfamiliar retained explanation\"}}") });
        }, observer);
        var error = await Assert.ThrowsAnyAsync<JsonException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        var context = GoogleLongAudioJournalFailure.Inspect(error); Assert.NotNull(context);
        Assert.Equal(400, context.ObservedHttpStatus); Assert.True(context.Observation.ReadbackVerified);
        Assert.Contains("Unfamiliar retained explanation", Assert.Single(store.Records).Response!.RedactedBody, StringComparison.Ordinal);
        fixture.ReplaceJournalText(request.AttemptId, "{", original); // Restore only this synthetic fixture's exact saved bytes.
        Assert.Equal(GoogleLongAudioState.PostClaimed, fixture.Journal.Load(request.AttemptId).State);
        var next = Request(); observer.Arm(next, true, clock.GetUtcNow().AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(next, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PartialBodyFailureKeepsActual400AndTheOriginalReadExceptionWithHonestCaptureFlags()
    {
        using var fixture = new Fixture(); var request = Request(); var store = new ProtectedStore(); var clock = new Clock();
        var observer = new GoogleLongAudioProtectedResponseObserver(store, clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1)); var original = new IOException("Synthetic read interrupted.");
        using var adapter = fixture.Adapter(new Admission(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StreamContent(new PartialStream(original)) }), observer);
        var observed = await Assert.ThrowsAsync<IOException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Same(original, observed);
        var checkpoint = fixture.Journal.Load(request.AttemptId); Assert.Equal(400, checkpoint.HttpStatus);
        Assert.Equal(GoogleLongAudioState.Failed, checkpoint.State);
        Assert.Equal(GoogleResponseReadState.Failed, checkpoint.Observation!.ReadState);
        Assert.True(checkpoint.Observation.Truncated); Assert.False(Assert.Single(store.Records).Response!.ReadCompleted);
        var next = Request(); observer.Arm(next, true, clock.GetUtcNow().AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(next, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOrOversizedBearerMaterialFailsBeforeHttpWithoutEchoingItsValue(bool oversized)
    {
        using var fixture = new Fixture(); var request = Request(); int calls = 0;
        string secret = oversized ? new string('a', 16_385) : "PRIVATE\r\nprivate-token";
        using var adapter = new GoogleLongAudioAdapter(new FixedCredentials(secret), new Admission(), fixture.Journal,
            new ReadyObserver(), new Handler((_, _) => { calls++; return Task.FromResult(Json(Operation(false))); }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls); Assert.Equal(GoogleLongAudioState.PostClaimed, fixture.Journal.Load(request.AttemptId).State);
    }

    [Fact]
    public async Task WithdrawalInsideCredentialResolutionStopsHttpAndRetainsStickyClaim()
    {
        using var fixture = new Fixture(); var request = Request(); var clock = new Clock();
        var observer = new GoogleLongAudioProtectedResponseObserver(new ProtectedStore(), clock);
        observer.Arm(request, true, clock.GetUtcNow().AddHours(1)); int calls = 0;
        using var adapter = new GoogleLongAudioAdapter(new WithdrawingCredentials(observer), new Admission(), fixture.Journal,
            observer, new Handler((_, _) => { calls++; return Task.FromResult(Json(Operation(false))); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls); Assert.Equal(GoogleLongAudioState.PostClaimed, fixture.Journal.Load(request.AttemptId).State);
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken); Assert.Equal(0, calls);
    }

    private sealed class WithdrawingCredentials(GoogleLongAudioProtectedResponseObserver observer) : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); observer.Disarm(); return ValueTask.FromResult("synthetic-token"); }
    }

    private sealed class FixedCredentials(string token) : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(token); }
    }

    private sealed class ThrowingObserver : IGoogleLongAudioResponseObserver
    {
        public bool IsReadyFor(GoogleLongAudioRequest request) => true;
        public Guid Begin(GoogleLongAudioRequest request) => Guid.NewGuid();
        public bool IsActiveFor(Guid ticket, GoogleLongAudioRequest request) => true;
        public Task<GoogleLongAudioObservation> ObserveAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
            ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken, bool operationError) =>
            throw new IOException("PRIVATE observer failure");
        public void FinishResponse(Guid ticket) => throw new IOException("PRIVATE cleanup failure");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceMonotonic(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public void RollBackUtc(TimeSpan elapsed) => _utc -= elapsed;
    }

    private sealed class PartialStream(IOException original) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sent) return ValueTask.FromException<int>(original);
            _sent = true; "{\"error\":"u8.CopyTo(buffer.Span); return ValueTask.FromResult(9);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ProtectedStore : IPrivateProviderErrorPersistence
    {
        public List<PrivateProviderErrorRecord> Records { get; } = [];
        public string Mode { get; init; } = string.Empty;
        public int Preparations { get; private set; }
        public int Readbacks { get; private set; }
        public bool PrepareProtectedCapture() { Preparations++; return true; }
        public bool Save(PrivateProviderErrorRecord record)
        {
            if (string.Equals(Mode, "save-throw", StringComparison.Ordinal)) throw new IOException("Synthetic private sink failed.");
            if (string.Equals(Mode, "save-false", StringComparison.Ordinal)) return false;
            Records.Add(record); return true;
        }
        public IReadOnlyList<PrivateProviderErrorRecord> Read() => Records;
        public PrivateProviderErrorReadResult ReadWithStatus()
        { Readbacks++; return new(PrivateProviderErrorReadStatus.Success, string.Equals(Mode, "read-missing", StringComparison.Ordinal) ? [] : Records); }
    }
}

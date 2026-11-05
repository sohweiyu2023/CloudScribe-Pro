using System.Net;
using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleLongAudioAdapterTests
{
    private static readonly string[] PrivateDetails = ["PRIVATE"];

    [Fact]
    public async Task PostOnceThenRestartPollsExactOperationWithoutAcceptingMedia()
    {
        using var fixture = new Fixture();
        var request = Request(); var gate = new Admission();
        int posts = 0, gets = 0;
        using (var adapter = fixture.Adapter(gate, async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); posts++;
            Assert.Equal("https://texttospeech.googleapis.com/v1/projects/123456/locations/global:synthesizeLongAudio", message.RequestUri!.AbsoluteUri);
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            Assert.Equal("synthetic article <&> \u4f60\u597d", wire.RootElement.GetProperty("input").GetProperty("text").GetString());
            Assert.Equal(request.OutputGcsUri, wire.RootElement.GetProperty("outputGcsUri").GetString());
            return Json(Operation(false));
        }))
        {
            Assert.Equal(GoogleLongAudioState.Pollable, (await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken)).State);
            Assert.Equal(GoogleLongAudioState.Pollable, (await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken)).State);
        }
        using var resumed = fixture.Adapter(gate, (message, _) =>
        {
            gets++; Assert.Equal(HttpMethod.Get, message.Method); Assert.Null(message.Content);
            Assert.Equal("https://texttospeech.googleapis.com/v1/" + Name, message.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(Operation(true)));
        });
        var result = await resumed.PollOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.CompletedUnverified, result.State);
        _ = await resumed.PollOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, posts); Assert.Equal(1, gets); Assert.Equal(1, gate.Calls);
        Assert.DoesNotContain("synthetic article", fixture.JournalText(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", fixture.JournalText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostPostResponseIsStickyAndCannotBeReboundOrRetriedAfterRestart()
    {
        using var fixture = new Fixture(); var request = Request(); var gate = new Admission(); int calls = 0;
        using var adapter = fixture.Adapter(gate, (_, _) =>
        { calls++; throw new HttpRequestException("synthetic network failure"); });
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(GoogleLongAudioState.PostClaimed, fixture.Journal.Load(request.AttemptId).State);
        using var restart = fixture.Adapter(gate, (_, _) => throw new InvalidOperationException("Must not send again."));
        Assert.Equal(GoogleLongAudioState.PostClaimed, (await restart.SubmitOnceAsync(request, TestContext.Current.CancellationToken)).State);
        _ = await restart.PollOnceAsync(request, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restart.SubmitOnceAsync(Request(request.AttemptId, "changed"), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.Equal(1, gate.Calls);
    }

    [Theory]
    [InlineData("{\"name\":\"projects/999/locations/global/operations/one\"}")]
    [InlineData("{\"name\":\"projects/123456/locations/eu/operations/one\"}")]
    [InlineData("{\"name\":\"bare-operation\"}")]
    [InlineData("{\"name\":\"projects/123456/locations/global/operations/one\",\"done\":false,\"done\":true}")]
    public async Task InvalidOrWrongScopeOperationLeavesUnknownClaimWithoutResubmission(string body)
    {
        using var fixture = new Fixture(); var request = Request(); int calls = 0;
        using var adapter = fixture.Adapter(new Admission(), (_, _) => { calls++; return Task.FromResult(Json(body)); });
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.True(fixture.Journal.Load(request.AttemptId).OperationParseFailed);
        Assert.Equal(GoogleLongAudioState.PostClaimed, (await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken)).State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TerminalOperationRetainsOnlySafeCodeAndNeverDownloadsTheOutput()
    {
        using var fixture = new Fixture(); var request = Request();
        using var adapter = fixture.Adapter(new Admission(), (_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        { name = Name, done = true, error = new { code = 3, message = "PRIVATE synthetic provider echo", details = PrivateDetails } }))));
        var result = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.Failed, result.State); Assert.Equal(3, result.RpcCode);
        Assert.DoesNotContain("PRIVATE", fixture.JournalText(), StringComparison.Ordinal);
        Assert.DoesNotContain(request.OutputGcsUri, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic article", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdmissionDenialAndCancelledPostCannotBecomeAutomaticRetries()
    {
        using var fixture = new Fixture(); var request = Request(); var gate = new Admission { Deny = true }; int calls = 0;
        using var adapter = fixture.Adapter(gate, (_, _) => { calls++; return Task.FromResult(Json(Operation(false))); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        gate.Deny = false;
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls); Assert.Equal(1, gate.Calls);
        var other = Request();
        using var otherFixture = new Fixture();
        using var cancelled = otherFixture.Adapter(gate, (_, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.SubmitOnceAsync(other, TestContext.Current.CancellationToken));
        Assert.Equal(GoogleLongAudioState.PostClaimed, otherFixture.Journal.Load(other.AttemptId).State);
    }

    [Fact]
    public async Task OversizedResponseAndHttpRejectionDoNotCreateAcceptedMedia()
    {
        using var fixture = new Fixture(); var request = Request();
        using var adapter = fixture.Adapter(new Admission(), (_, _) => Task.FromResult(Json(new string('x', 16_385))));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken));
        var denied = Request();
        using var otherFixture = new Fixture();
        using var rejection = otherFixture.Adapter(new Admission(), (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("PRIVATE provider body") }));
        var result = await rejection.SubmitOnceAsync(denied, TestContext.Current.CancellationToken);
        Assert.Equal(GoogleLongAudioState.Failed, result.State); Assert.Equal(400, result.HttpStatus);
        Assert.Equal(result, otherFixture.Journal.Load(denied.AttemptId));
        Assert.DoesNotContain("PRIVATE", otherFixture.JournalText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartRejectsChangedJournalScopeEvenIfOperationNameAlsoChanges()
    {
        using var fixture = new Fixture(); var request = Request(); int calls = 0;
        using var adapter = fixture.Adapter(new Admission(), (_, _) => { calls++; return Task.FromResult(Json(Operation(false))); });
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        fixture.ReplaceJournalText(request.AttemptId, "123456", "654321");
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.PollOnceAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RequestRejectsTraversalWrongOutputIdentityAndInvalidUnicode()
    {
        Guid id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/../" + id.ToString("N"), "text", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3"));
        Assert.Throws<ArgumentException>(() => GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/other.mp3", "text", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3"));
        Assert.Throws<ArgumentException>(() => GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/output/" + id.ToString("N") + "_" + Guid.NewGuid().ToString("N") + ".mp3",
            "text", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3"));
        Assert.Throws<ArgumentException>(() => GoogleLongAudioRequest.Create(id, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/output/" + id.ToString("N") + ".wav", "text", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3"));
        Assert.Throws<EncoderFallbackException>(() => Request(id, "\ud800"));
    }

    private const string Name = "projects/123456/locations/global/operations/synthetic-operation";
    private static string Operation(bool done) => JsonSerializer.Serialize(new { name = Name, done });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static GoogleLongAudioRequest Request(Guid? id = null, string text = "synthetic article <&> \u4f60\u597d")
    {
        Guid value = id ?? Guid.NewGuid();
        return GoogleLongAudioRequest.Create(value, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/output/" + value.ToString("N") + ".mp3", text, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3");
    }

    private sealed class Admission : IGoogleLongAudioAdmission
    {
        public int Calls { get; private set; }
        public bool Deny { get; set; }
        public ValueTask RequireSubmissionAuthorizedAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; if (Deny) throw new InvalidOperationException("Synthetic readiness gate denied."); return ValueTask.CompletedTask; }
    }

    private sealed class Credentials : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Assert.Equal("synthetic-ref", credentialReferenceId); return ValueTask.FromResult("synthetic-token"); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ReadyObserver : IGoogleLongAudioResponseObserver
    {
        public bool IsReadyFor(GoogleLongAudioRequest request) => true;
        public Guid Begin(GoogleLongAudioRequest request) => Guid.NewGuid();
        public bool IsActiveFor(Guid ticket, GoogleLongAudioRequest request) => true;
        public void FinishResponse(Guid ticket) { }
        public Task<GoogleLongAudioObservation> ObserveAsync(Guid ticket, GoogleLongAudioRequest request, int httpStatus,
            ReadOnlyMemory<byte> boundedBody, GoogleResponseReadState readState, string transientAccessToken, bool operationError) =>
            Task.FromResult(new GoogleLongAudioObservation(Guid.NewGuid(), readState, GoogleLongAudioParseState.Json,
                GoogleErrorStatus.NotProvided, boundedBody.Length, GoogleExplanationRetentionState.SavedPrivately, true, false, false));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudscribe-long-audio-tests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Journal = new GoogleLongAudioFileJournal(_directory);
        public GoogleLongAudioFileJournal Journal { get; }
        public GoogleLongAudioAdapter Adapter(Admission gate, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            IGoogleLongAudioResponseObserver? observer = null) =>
            new(new Credentials(), gate, Journal, observer ?? new ReadyObserver(), new Handler(send));
        public string JournalText() => string.Join('\n', Directory.EnumerateFiles(_directory, "*.json").Select(File.ReadAllText));
        public void ReplaceJournalText(Guid id, string before, string after)
        {
            string path = Path.Combine(_directory, id.ToString("N") + ".json");
            File.WriteAllText(path, File.ReadAllText(path).Replace(before, after, StringComparison.Ordinal));
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

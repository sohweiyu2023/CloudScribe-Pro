using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task DefaultOffNeverCreatesPrivateStore()
    {
        using var f = new Fixture(); await f.SendAsync("An unfamiliar provider limit was encountered.");
        Assert.False(Directory.Exists(f.Root)); Assert.False(f.Store.IsArmed);
    }

    [Fact]
    public async Task NovelMessageSurvivesPrivatelyAcrossRestartWithoutChangingOutcome()
    {
        using var f = new Fixture(); f.Arm();
        const string diagnostic = "A novel synthesis validation failed at boundary 17; consult a provider administrator.";
        var result = await f.SendAsync(diagnostic);
        Assert.Equal("google-http-400", result.DiagnosticCode); Assert.Equal(GoogleErrorStatus.InvalidArgument, result.ErrorStatus);
        Assert.False(result.IsAccepted); Assert.Empty(result.MediaBytes.ToArray()); Assert.Equal(1, f.Handler.Calls);
        var saved = Assert.Single(new GooglePrivateErrorDiagnostics(f.Root, f.Clock).ReadRecords());
        Assert.Equal(diagnostic, saved.Message); Assert.Equal(f.Binding.PayloadSha256, saved.PayloadSha256);
        Assert.True(Guid.TryParseExact(saved.TransportCorrelation, "N", out _));
        Assert.False(f.Store.IsArmed);
    }

    [Fact]
    public async Task TicketIsBoundToExactRequestObjectAndSavedDocument()
    {
        using var f = new Fixture(); f.Store.Arm(f.Binding, true);
        f.Store.Attach(f.Request, f.Binding with { DocumentId = Guid.NewGuid() });
        await f.SendAsync("Private result"); Assert.Empty(f.Store.ReadRecords());
        f.Arm();
        var sameBytes = new GenerationProviderRequest(f.Request.ProviderStableId, f.Request.OperationStableId,
            f.Request.AccountId, f.Request.IdempotencyKey, f.Request.CompiledPayload, f.Request.OutputFormat);
        await f.Provider.SubmitAsync(sameBytes, TestContext.Current.CancellationToken);
        Assert.Empty(f.Store.ReadRecords());
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("version")]
    [InlineData("voice")]
    [InlineData("project")]
    [InlineData("account")]
    [InlineData("payload")]
    public async Task ChangedIdentityDiscardsOptIn(string field)
    {
        using var f = new Fixture(); var b = f.Binding;
        f.Store.Arm(b, true);
        var changed = field switch
        {
            "revision" => b with { RevisionId = Guid.NewGuid() },
            "version" => b with { RequestVersion = 8 },
            "voice" => b with { VoiceId = "another" },
            "project" => b with { ProjectId = "another" },
            "account" => b with { AccountId = "another" },
            _ => b with { PayloadSha256 = new string('a', 64) },
        };
        f.Store.Attach(f.Request, changed); await f.SendAsync("Unknown message"); Assert.Empty(f.Store.ReadRecords());
    }

    [Fact]
    public async Task OneShotCannotCaptureAnotherResponseAndDoesNotRearmOnRestart()
    {
        using var f = new Fixture(); f.Arm(); await f.SendAsync("First unknown message");
        await f.SendAsync("Second unknown message");
        Assert.Equal("First unknown message", Assert.Single(f.Store.ReadRecords()).Message);
        Assert.False(new GooglePrivateErrorDiagnostics(f.Root, f.Clock).IsArmed);
    }

    [Fact]
    public async Task ExpiryDisarmAndSuccessCannotLeaveAnArmedFutureCapture()
    {
        using var f = new Fixture(); f.Arm(); f.Clock.Now += TimeSpan.FromMinutes(11);
        await f.SendAsync("Expired message"); Assert.Empty(f.Store.ReadRecords());
        f.Arm(); f.Store.Disarm(); await f.SendAsync("Cancelled opt-in"); Assert.Empty(f.Store.ReadRecords());
        f.Arm(); f.Handler.Status = HttpStatusCode.OK; await f.SendAsync("Success body is not a private error");
        f.Handler.Status = HttpStatusCode.BadRequest; await f.SendAsync("Later error"); Assert.Empty(f.Store.ReadRecords());
    }

    [Fact]
    public async Task RotationAndExpiryBoundActivePrivateStorage()
    {
        using var f = new Fixture();
        for (int i = 0; i < 10; i++) { f.Clock.Now += TimeSpan.FromSeconds(1); f.Arm(); await f.SendAsync("Unknown message " + i); }
        Assert.Equal(8, f.Store.ReadRecords().Count);
        Assert.All(Directory.GetFiles(f.Root), path => Assert.True(new FileInfo(path).Length <= 4096));
        f.Clock.Now += TimeSpan.FromHours(24); Assert.Empty(f.Store.ReadRecords()); Assert.Empty(Directory.GetFiles(f.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cloudscribe-private-error-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public Handler Handler { get; } = new();
        public GooglePrivateErrorDiagnostics Store { get; }
        public GenerationProviderRequest Request { get; }
        public PrivateErrorCaptureBinding Binding { get; }
        public GoogleGenerationProvider Provider { get; }
        private readonly HttpClient _http;
        public Fixture(IPrivateProviderErrorPersistence? persistence = null, ILogger<GoogleGenerationHttpTransport>? logger = null)
        {
            Store = persistence is null ? new(Root, Clock) : new(persistence, Clock);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = "SYNTHETIC PRIVATE INPUT DOCUMENT WITH A LONG SECRET FRAGMENT" } });
            Request = new(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-account", "synthetic-key", payload, "mp3");
            Binding = new(Guid.NewGuid(), Guid.NewGuid(), 7, "synthetic-account", "synthetic-project", "synthetic-voice", "synthetic-reference", Convert.ToHexStringLower(SHA256.HashData(payload)));
            _http = new HttpClient(Handler);
            var transport = new GoogleGenerationHttpTransport(_http, new Credential(), new Uri("https://texttospeech.googleapis.com"), logger, Store);
            Provider = new(new("synthetic-account", "synthetic-reference", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"), transport);
        }
        public void Arm() { Store.Arm(Binding, true); Store.Attach(Request, Binding); }
        public async Task<GenerationProviderResponse> SendAsync(string text)
        {
            Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = text } });
            var result = await Provider.SubmitAsync(Request, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            return result;
        }
        public void Dispose()
        {
            Store.Dispose();
            _http.Dispose();
            if (!Directory.Exists(Root)) return;
            foreach (var file in new DirectoryInfo(Root).GetFiles())
            {
                if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refuse linked fixture cleanup.");
                file.Delete();
            }
            Directory.Delete(Root, recursive: false);
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        public long Timestamp { get; set; } = TimeSpan.TicksPerDay;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
    private sealed class Credential : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string reference, CancellationToken token) => ValueTask.FromResult("SYNTHETIC_SECRET_ACCESS_TOKEN");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Body { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.BadRequest;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Exception? Failure { get; set; }
        public Action? BeforeResponse { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls); BeforeResponse?.Invoke();
            return Failure is { } error ? Task.FromException<HttpResponseMessage>(error)
                : Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}

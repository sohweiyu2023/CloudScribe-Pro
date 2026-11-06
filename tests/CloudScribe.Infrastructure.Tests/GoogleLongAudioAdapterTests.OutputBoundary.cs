using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

// Compile beside the existing adapter tests when the parent schedules the offline test build.
public sealed partial class GoogleLongAudioAdapterTests
{
    [Fact]
    public async Task InterruptedUnpublishedIdentityDoesNotPoisonCompletedOutputRecovery()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(output.OutputRoot);
        string interrupted = Path.Combine(output.OutputRoot, request.AttemptId.ToString("N") + ".object.json.crash.pending");
        File.WriteAllText(interrupted, "{partial synthetic identity");
        int gets = 0;
        using var boundary = output.Boundary(f.Journal, (_, _) =>
        {
            gets++;
            return Task.FromResult(gets == 1 ? Json(OutputMetadata(request, output.Audio))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.Audio) });
        });
        var result = await boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result.LocalPath)); Assert.True(File.Exists(interrupted));
        Assert.Equal(2, gets);
    }

    [Fact]
    public async Task GenerationPinnedOutputIsDownloadedVerifiedAndRestartedWithoutAnotherGetOrPost()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        int gets = 0;
        Task<HttpResponseMessage> Read(HttpRequestMessage message, CancellationToken _)
        {
            gets++; Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("storage.googleapis.com", message.RequestUri!.Host);
            if (gets == 1) return Task.FromResult(Json(OutputMetadata(request, output.Audio)));
            Assert.Contains("generation=123&ifGenerationMatch=123&alt=media", message.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.Audio) });
        }
        GoogleLongAudioVerifiedArtifact first;
        using (var boundary = output.Boundary(f.Journal, Read)) first = await boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(output.Audio)), first.AudioSha256);
        Assert.Equal("123", first.Identity.Generation); Assert.Equal(2, gets);
        using var restart = output.Boundary(f.Journal, (_, _) => throw new InvalidOperationException("No repeated object read expected."));
        var again = await restart.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(first, again); Assert.Equal(4, output.Native.Calls);
        Assert.Equal(GoogleLongAudioState.CompletedUnverified, f.Journal.Load(request.AttemptId).State);
        Assert.DoesNotContain(request.OutputGcsUri, first.ToString(), StringComparison.Ordinal);
        File.WriteAllBytes(first.LocalPath, new byte[output.Audio.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => restart.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[output.Audio.Length], File.ReadAllBytes(first.LocalPath)); // Never overwrite a mismatched local file.
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("generation")]
    [InlineData("checksum")]
    [InlineData("size")]
    [InlineData("decode")]
    public async Task OutputMismatchCannotBecomeAVerifiedArtifact(string fault)
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        output.Native.FailDecode = string.Equals(fault, "decode", StringComparison.Ordinal);
        int calls = 0;
        using var boundary = output.Boundary(f.Journal, (_, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Json(OutputMetadata(request, output.Audio, fault)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.Audio) });
        });
        await Assert.ThrowsAnyAsync<Exception>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(GoogleLongAudioState.CompletedUnverified, f.Journal.Load(request.AttemptId).State);
        Assert.InRange(calls, 1, 2);
    }

    [Fact]
    public async Task StorageFailurePreservesActualHttpStatusAndSafeRetentionMetadata()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        using var boundary = output.Boundary(f.Journal, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("{\"error\":{\"message\":\"PRIVATE STORAGE SENTINEL\"}}") }));
        var failure = await Assert.ThrowsAsync<GoogleLongAudioStorageReadException>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(403, failure.HttpStatus); Assert.True(failure.Observation.ReadbackVerified);
        Assert.DoesNotContain("PRIVATE", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(failure.Observation), StringComparison.Ordinal);
        Assert.Equal(0, output.Native.Calls);
    }

    [Fact]
    public async Task PendingOperationCannotReadAnObjectOrCreateAnAcceptedArtifact()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(false))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken);
        using var boundary = output.Boundary(f.Journal, (_, _) => throw new InvalidOperationException("No GET expected."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, output.Native.Calls);
    }

    [Fact]
    public async Task MissingReadAuthorityStopsBeforeAnyStorageGet()
    {
        using var f = new Fixture(); using var output = new OutputFixture(); var request = Request();
        using var adapter = f.Adapter(new Admission(), (_, _) => Task.FromResult(Json(Operation(true))));
        _ = await adapter.SubmitOnceAsync(request, TestContext.Current.CancellationToken); output.Authority.Deny = true;
        using var boundary = output.Boundary(f.Journal, (_, _) => throw new InvalidOperationException("No GET expected."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.DownloadAndVerifyAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, output.Native.Calls);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Synthetic GCS checksum fixture only, not authentication.")]
    private static string OutputMetadata(GoogleLongAudioRequest request, byte[] audio, string fault = "") => JsonSerializer.Serialize(new
    {
        bucket = string.Equals(fault, "bucket", StringComparison.Ordinal) ? "other-private" : "synthetic-private",
        name = "output/" + request.AttemptId.ToString("N") + ".mp3",
        generation = string.Equals(fault, "generation", StringComparison.Ordinal) ? "0" : "123",
        size = (audio.Length + (string.Equals(fault, "size", StringComparison.Ordinal) ? 1 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        md5Hash = Convert.ToBase64String(MD5.HashData(string.Equals(fault, "checksum", StringComparison.Ordinal) ? "different"u8.ToArray() : audio))
    });

    private sealed class OutputAuthority : IGoogleLongAudioOutputAuthority
    {
        public bool Deny { get; set; }
        public DateTimeOffset? Deadline { get; set; }
        public ValueTask<GoogleLongAudioOutputAllowance> RequireReadAuthorizedAsync(GoogleLongAudioRequest request,
            GoogleLongAudioCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (Deny) throw new InvalidOperationException("Synthetic read authority denied.");
            return ValueTask.FromResult(new GoogleLongAudioOutputAllowance(4096, Deadline, "mp3", 24000, 1));
        }
    }

    private sealed class OutputFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "cloudscribe-long-output-tests-" + Guid.NewGuid().ToString("N"));
        private readonly GoogleLongAudioMediaTools tools;
        public OutputNative Native { get; } = new();
        public OutputAuthority Authority { get; } = new();
        public byte[] Audio { get; } = new byte[128];
        public string OutputRoot => Path.Combine(directory, "output");
        public OutputFixture()
        {
            Directory.CreateDirectory(directory); "ID3"u8.CopyTo(Audio);
            string tool = Path.Combine(directory, "synthetic-tool.exe"); File.WriteAllBytes(tool, "synthetic native identity"u8.ToArray());
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(tool)));
            tools = new(tool, hash, tool, hash);
        }
        public GoogleLongAudioOutputBoundary Boundary(GoogleLongAudioFileJournal journal,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            IGoogleLongAudioResponseObserver? observer = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
            new(new Credentials(), Authority, journal, Native, tools, Path.Combine(directory, "output"),
                TimeProvider.System, _ => observer ?? new ReadyObserver(), new Handler(handler), logger);
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class OutputNative : INativeMediaTool
    {
        public int Calls { get; private set; }
        public bool FailDecode { get; set; }
        public Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Calls++; Assert.Contains("file,pipe", invocation.Arguments, StringComparer.Ordinal); Assert.Contains("mp3", invocation.Arguments, StringComparer.Ordinal);
            bool probe = invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal);
            if (!probe) Assert.Contains("-xerror", invocation.Arguments, StringComparer.Ordinal);
            string json = "{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"mp3\",\"sample_rate\":\"24000\",\"channels\":1}],\"format\":{\"duration\":\"1\"}}";
            return Task.FromResult(new NativeMediaToolResult(!probe && FailDecode ? 1 : 0, false, probe ? json : "", "", TimeSpan.Zero));
        }
    }
}

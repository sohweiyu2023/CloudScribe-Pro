using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task LongAudioComposedRuntimeSubmitsOncePollsVerifiesAndRestartsWithoutAnotherChargeOrGet()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        var allowance = new GoogleLongAudioOutputAllowance(4096, null, "mp3", 24000, 1);
        byte[] ledgerBefore = File.ReadAllBytes(p.LedgerPath);
        using (var session = await fixture.Factory.OpenAsync(p.Request, fixture.Tools, allowance, true, TestContext.Current.CancellationToken))
        {
            Assert.False(session.Review.IsAlreadyCommitted); Assert.Equal(1, session.Review.AdditionalCommitmentUsdCents);
            Assert.Equal(ledgerBefore, File.ReadAllBytes(p.LedgerPath));
            Assert.Equal(GoogleLongAudioState.Pollable, (await session.AdvanceAsync(TestContext.Current.CancellationToken)).Checkpoint.State);
            var done = await session.PollAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(done.Artifact); Assert.True(File.Exists(done.Artifact.LocalPath));
            Assert.Equal(4, fixture.Transport.Calls); Assert.Equal(1, fixture.Transport.Posts);
            Assert.Equal(3, fixture.Capture.Records.Count); // POST, terminal operation and bounded object metadata.
        }
        byte[] after = File.ReadAllBytes(p.LedgerPath);
        using var restart = await fixture.Factory.OpenAsync(p.Request, fixture.Tools, allowance, true, TestContext.Current.CancellationToken);
        Assert.True(restart.Review.IsAlreadyCommitted); Assert.Equal(0, restart.Review.AdditionalCommitmentUsdCents);
        Assert.NotNull((await restart.AdvanceAsync(TestContext.Current.CancellationToken)).Artifact);
        Assert.Equal(4, fixture.Transport.Calls); Assert.Equal(1, fixture.Transport.Posts);
        Assert.Equal(after, File.ReadAllBytes(p.LedgerPath));
    }

    [Theory]
    [InlineData("changed-tool")]
    [InlineData("expired-read")]
    public async Task LongAudioRuntimeRechecksAfterOpenBeforeClaimOrHttp(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        var allowance = new GoogleLongAudioOutputAllowance(4096, e.Clock.GetUtcNow().AddSeconds(1), "mp3", 24000, 1);
        using var session = await fixture.Factory.OpenAsync(p.Request, fixture.Tools, allowance, true, TestContext.Current.CancellationToken);
        byte[] before = File.ReadAllBytes(p.LedgerPath);
        if (fault is "changed-tool") File.AppendAllText(fixture.Tools.FfmpegPath, "changed");
        else e.Clock.Now = e.Clock.Now.AddSeconds(2);
        await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls); Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(p.Root, "operations")));
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("changed-tool")]
    [InlineData("expired-read")]
    public async Task LongAudioRuntimeLocalCorrectionStopsBeforeCommitmentOrAnyHttp(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, p.Request);
        var allowance = new GoogleLongAudioOutputAllowance(4096, null, "mp3", 24000, 1);
        if (fault is "expired-read") allowance = allowance with { ReadNotAfterUtc = e.Clock.GetUtcNow().AddSeconds(-1) };
        if (fault is "changed-tool") File.AppendAllText(fixture.Tools.FfmpegPath, "changed");
        byte[] before = File.ReadAllBytes(p.LedgerPath);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Factory.OpenAsync(p.Request, fixture.Tools, allowance, fault is not "declined", TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls); Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
        Assert.False(Directory.Exists(Path.Combine(p.Root, "operations")));
    }

    private static async Task<(GoogleLongAudioRuntimeFactory Factory, GoogleLongAudioMediaTools Tools,
        LongRuntimeTransport Transport, LongRuntimeCapture Capture)> PrepareLongRuntimeAsync(Fixture f, ChirpEvidence e,
        string app, GoogleLongAudioRequest request, Func<HttpMessageHandler>? recoveryTransport = null, INativeMediaTool? recoveryNative = null,
        Microsoft.Extensions.Logging.ILoggerFactory? recoveryLogs = null)
    {
        var vault = new LongRuntimeVault(); var onboarding = new GoogleServiceAccountCredentialOnboardingService(vault);
        string json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "synthetic-project",
            client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
            private_key_id = "synthetic-key",
            private_key = "-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----",
            token_uri = "https://oauth2.googleapis.com/token"
        });
        await onboarding.ImportAsync(request.CredentialReferenceId, json.AsMemory(), new Uri("https://texttospeech.googleapis.com/v1/voices"),
            new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var reference = new ProviderAccountReference(GoogleGenerationProvider.StableProviderId, "synthetic-account", "Synthetic account",
            new CredentialReference(request.CredentialReferenceId), endpointOrigin: new Uri("https://texttospeech.googleapis.com"));
        var snapshot = new ProviderAccountSnapshot(reference, true, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var accounts = new LongRuntimeAccounts(snapshot); var transport = new LongRuntimeTransport(request); var capture = new LongRuntimeCapture();
        string tool = Path.Combine(f.Root, "inert-long-tool.exe"); File.WriteAllText(tool, "inert synthetic verifier"); string hash = Hash("inert synthetic verifier");
        var tools = new GoogleLongAudioMediaTools(tool, hash, tool, hash);
        var factory = new GoogleLongAudioRuntimeFactory(new(app, f.Paths, e.Clock), new(accounts, onboarding), new LongRuntimeCredentials(),
            capture, recoveryNative ?? new LongRuntimeNative(), e.Clock, recoveryTransport ?? (() => new LongRuntimeHandler(transport)), recoveryLogs);
        return (factory, tools, transport, capture);
    }

    private sealed class LongRuntimeAccounts(ProviderAccountSnapshot snapshot) : IProviderAccountStore
    {
        public Task<ProviderAccountSnapshot?> FindAsync(string providerStableId, string accountId, CancellationToken cancellationToken = default) => Task.FromResult<ProviderAccountSnapshot?>(snapshot);
        public Task<ProviderAccountSnapshot> CreateAsync(ProviderAccountReference account, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderAccountSnapshot> UpdateAsync(ProviderAccountReference account, bool isEnabled, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderAccountSnapshot>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class LongRuntimeVault : ICredentialVault
    {
        private readonly Dictionary<string, char[]> values = new(StringComparer.Ordinal);
        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        { values.Add(reference.TargetName, secret.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(values.TryGetValue(reference.TargetName, out var value) ? new CredentialSecret(value.ToArray()) : null);
        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) => ValueTask.FromResult(values.Remove(reference.TargetName));
    }
    private sealed class LongRuntimeCredentials : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) => ValueTask.FromResult("synthetic-token");
    }
    private sealed class LongRuntimeCapture : IPrivateProviderErrorPersistence
    {
        public List<PrivateProviderErrorRecord> Records { get; } = [];
        public bool PrepareProtectedCapture() => true;
        public bool Save(PrivateProviderErrorRecord record) { Records.Add(record); return true; }
        public IReadOnlyList<PrivateProviderErrorRecord> Read() => Records.ToArray();
    }
    private sealed class LongRuntimeNative : INativeMediaTool
    {
        public Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NativeMediaToolResult(0, false, invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal)
                ? "{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"mp3\",\"sample_rate\":\"24000\",\"channels\":1}],\"format\":{\"duration\":\"1\"}}" : "", "", TimeSpan.Zero));
    }
    private sealed class LongRuntimeHandler(LongRuntimeTransport transport) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(transport.Send(request));
    }
    private sealed class LongRuntimeTransport(GoogleLongAudioRequest request)
    {
        public int Calls { get; private set; }
        public int Posts { get; private set; }
        private readonly byte[] audio = "ID3 synthetic bounded fixture bytes for fake media verifier"u8.ToArray();
        public HttpResponseMessage Send(HttpRequestMessage message)
        {
            Calls++;
            if (message.Method == HttpMethod.Post) { Posts++; return Json(new { name = "projects/123456/locations/global/operations/synthetic", done = false }); }
            if (string.Equals(message.RequestUri!.Host, "texttospeech.googleapis.com", StringComparison.Ordinal))
                return Json(new { name = "projects/123456/locations/global/operations/synthetic", done = true });
            if (message.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            return Metadata();
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Synthetic GCS MD5 checksum only, not authentication.")]
        private HttpResponseMessage Metadata() => Json(new
        {
            bucket = "synthetic-private",
            name = "output/" + request.AttemptId.ToString("N") + ".mp3",
            generation = "123",
            size = audio.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            md5Hash = Convert.ToBase64String(MD5.HashData(audio))
        });
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}

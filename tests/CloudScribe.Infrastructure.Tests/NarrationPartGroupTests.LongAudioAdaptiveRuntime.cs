using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "none")]
    [InlineData(true, true, "none")]
    [InlineData(true, false, "encode")]
    [InlineData(true, false, "receipt")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One isolated composition proves real installed-scope, ledger, journal, capture, artifact-receipt and recovery integration with fake HTTP/native boundaries.")]
    public async Task LongAudioAdaptiveComposedRuntimePreservesAcceptedChildThenRecoversSiblingAndRestartsAtExactBudget(bool family, bool pollable, string localFailure)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic adaptive", "Alpha one.\n\nBeta two.\n\nGamma three.\n\nDelta four.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var root = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            p.Request.OutputGcsUri.Replace(".mp3", ".wav", StringComparison.Ordinal), document.Text, scope.LanguageCode, scope.VoiceName, "LINEAR16");
        var range = new ChirpNarrationRange(0, document.Text.EnumerateRunes().Count(), Hash(document.Text), null);
        var first = GoogleLongAudioRecoveryPlanner.Split(document.Text, range, root, AdaptiveRejected(root), false, 30);
        var second = GoogleLongAudioRecoveryPlanner.Split(document.Text, first[1].Range, first[1].Request, AdaptiveRejected(first[1].Request), false, 30);
        var requests = new[] { root, first[0].Request, first[1].Request, second[0].Request, second[1].Request };
        scope = scope with
        {
            AudioEncoding = "LINEAR16",
            TotalGoogleCapUsdCents = 919,
            ApprovedRequestCeilingsUsdCents = (family ? [root] : requests).ToDictionary(x => x.BindingSha256, _ => 1L, StringComparer.Ordinal),
            AdaptiveRecoveryRootBindings = family ? [root.BindingSha256] : null,
            DocumentJobs = [scope.DocumentJobs![0] with { OutputGcsUri = root.OutputGcsUri }]
        };
        File.WriteAllBytes(p.ScopePath, JsonSerializer.SerializeToUtf8Bytes(scope)); string scopeSha = Hash(File.ReadAllText(p.ScopePath));
        string install = Path.GetDirectoryName(p.App)!, markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(markerPath))!; marker["GoogleLongAudioScopeSha256"] = scopeSha;
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, scopeSha, [])));
        var parent = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(p.ParentPath))!;
        WriteSyntheticLongProvisioningProof(install, parent, scope.ParentPolicySha256, scopeSha, p.LedgerPath, e.Clock.GetUtcNow());
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(history));
        var control = new GoogleLongAudioRunControl();
        string? nativeDirectory = Environment.GetEnvironmentVariable("CLOUDSCRIBE_SYNTHETIC_LONG_AUDIO_MEDIA_TOOLS");
        INativeMediaTool? realNative = nativeDirectory is null ? null : new BoundedNativeMediaTool();
        var transport = new AdaptiveTransport(requests, [root.AttemptId, first[1].Request.AttemptId],
            pollable ? first[0].Request.AttemptId : null, control.RequestStop, realNative is null ? null : AdaptivePcm());
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, root, () => new AdaptiveHandler(transport), new AdaptiveNative(control, realNative), logs);
        var tools = nativeDirectory is null ? fixture.Tools : await AdaptiveActualToolsAsync(nativeDirectory);
        var runner = await fixture.Factory.CreateRecoveryRunnerAsync(root, tools, new(4096, null, "pcm_s16le", 24000, 1), true, history, TestContext.Current.CancellationToken);
        var stopped = await runner.RunAsync(root, control, token: TestContext.Current.CancellationToken);
        Assert.False(stopped.CoverageComplete); Assert.Equal(pollable ? 0 : 1, stopped.Outputs.Count); Assert.Equal(2, transport.Posts);
        var resumed = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.True(resumed.CoverageComplete); Assert.Equal(3, resumed.Outputs.Count); Assert.Equal(5, transport.Posts);
        Assert.Equal(pollable ? 1 : 0, transport.Polls);
        Assert.Equal(document.Text, string.Concat(resumed.Outputs.Select(x => ChirpNarrationPlanner.Slice(document.Text, x.Range))));
        var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Equal(5, ledger.Claims.Sum(x => x.MaximumUsdCents)); // 714+200+5=919; zero remaining, all holds retained.
        if (family)
        {
            Assert.Equal(4, ledger.Claims.Count(x => x.Recovery is not null));
            if (!pollable && localFailure is "none" && realNative is null)
            {
                byte[] original = File.ReadAllBytes(p.LedgerPath);
                foreach (string fault in new[] { "valid", "range", "parent", "planner", "amount", "missing", "extra" })
                {
                    var readback = await ReadAdaptiveClaimsWithPowerShellAsync(f, p.App, p.ScopePath, scopeSha, fault);
                    Assert.True(fault is "valid" ? readback.Exit == 0 : readback.Exit != 0, readback.Error);
                    Assert.Equal(original, File.ReadAllBytes(p.LedgerPath));
                }
            }
            var assemblyNative = new AdaptiveAssemblyFaultNative(realNative ?? new AdaptiveAssemblyNative(), localFailure is "encode");
            var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new CloudScribe.Infrastructure.Files.DocumentContentStore(f.Paths),
                fixture.Factory, e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history, assemblyNative);
            var review = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
            Assert.True(review.AdaptiveRecoveryEnabled);
            Assert.Equal(GoogleLongAudioContinuationKind.ResumeExistingOperation,
                (await service.InspectContinuationAsync(review, TestContext.Current.CancellationToken)).Kind);
            int beforeAssembly = transport.Calls;
            string final = Path.Combine(p.Root, "output", root.AttemptId.ToString("N") + ".article.mp3");
            string partial = Path.Combine(final + ".work", "part-00000000.wav.crash.pending");
            Directory.CreateDirectory(final + ".work"); File.WriteAllText(partial, "interrupted owned synthetic snapshot");
            if (localFailure is "receipt") Directory.CreateDirectory(final + ".verified.json");
            if (localFailure is not "none")
            {
                byte[] held = File.ReadAllBytes(p.LedgerPath);
                var failure = await Record.ExceptionAsync(() => service.RunAsync(review, tools, true, new(), token: TestContext.Current.CancellationToken));
                if (localFailure is "encode") Assert.IsType<InvalidDataException>(failure);
                else Assert.IsAssignableFrom<IOException>(failure);
                Assert.Equal(beforeAssembly, transport.Calls); Assert.Equal(held, File.ReadAllBytes(p.LedgerPath));
                if (localFailure is "receipt") { Assert.True(File.Exists(final)); Directory.Delete(final + ".verified.json"); }
            }
            var complete = await service.RunAsync(review, tools, true, new(), token: TestContext.Current.CancellationToken);
            Assert.Equal(LongAudioWorkflowStage.Completed, complete.Stage); Assert.NotNull(complete.ArticleOutput);
            Assert.Null(complete.Outcome.Artifact); // Never masquerades as an accepted provider/parent artifact.
            Assert.Equal(3, complete.ArticleOutput.Ordered.Count); Assert.True(File.Exists(complete.ArticleOutput.Mp3Path));
            Assert.Equal(beforeAssembly, transport.Calls);
            Assert.True(File.Exists(partial)); Assert.Equal(localFailure is "encode" ? 2 : 1, assemblyNative.Encodes);
            var reopened = await service.RunAsync(review, tools, true, new(), token: TestContext.Current.CancellationToken);
            Assert.Equal(complete.ArticleOutput.Sha256, reopened.ArticleOutput!.Sha256);
            Assert.Equal(beforeAssembly, transport.Calls);
        }
        int calls = transport.Calls; byte[] before = File.ReadAllBytes(p.LedgerPath);
        var restarted = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.True(restarted.CoverageComplete); Assert.Equal(resumed.Outputs, restarted.Outputs);
        Assert.Equal(5, transport.Posts); Assert.Equal(calls, transport.Calls); Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
        using (var reader = new StreamReader(new FileStream(Path.Combine(f.Root, "data", "google-trend-history-v1.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            string safeHistory = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Contains("LongAudioRecovery", safeHistory, StringComparison.Ordinal);
            Assert.Contains("SplitConfirmedRejection", safeHistory, StringComparison.Ordinal);
            Assert.DoesNotContain("Alpha one", safeHistory, StringComparison.Ordinal); Assert.DoesNotContain("synthetic-token", safeHistory, StringComparison.Ordinal);
            var traces = safeHistory.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x)).ToArray();
            try
            {
                var recovery = traces.Where(x => x.RootElement.TryGetProperty("EventId", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 1620).ToArray();
                Assert.NotEmpty(recovery);
                Assert.All(recovery, x => Assert.True(x.RootElement.GetProperty("FieldsComplete").GetBoolean()));
            }
            finally { foreach (var trace in traces) trace.Dispose(); }
        }
        string output = Path.Combine(p.Root, "output"); Assert.Equal(3, Directory.GetFiles(output, "*.recovery-verified.json").Length);
        File.AppendAllText(resumed.Outputs[0].Artifact.LocalPath, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
        Assert.Equal(calls, transport.Calls); Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
    }

    private static GoogleLongAudioCheckpoint AdaptiveRejected(GoogleLongAudioRequest request) => new(request.AttemptId, request.BindingSha256,
        request.ProjectNumber, request.Location, request.CredentialReferenceId, request.OutputGcsUri, request.PayloadSha256, request.PayloadBytes,
        GoogleLongAudioState.Failed, "projects/123456/locations/global/operations/synthetic", 200, 3, 4)
    {
        FailureKind = GoogleLongAudioFailureKind.SentenceLengthReported,
        Observation = new(Guid.NewGuid(), GoogleResponseReadState.Complete, GoogleLongAudioParseState.Json, GoogleErrorStatus.NotProvided,
            250, GoogleExplanationRetentionState.SavedPrivately, true, false, false)
    };

    private sealed class AdaptiveHandler(AdaptiveTransport transport) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            transport.SendAsync(request, cancellationToken);
    }
    private sealed class AdaptiveNative(GoogleLongAudioRunControl stopFirst, INativeMediaTool? actual = null) : INativeMediaTool
    {
        private bool stopped;
        public async Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            bool probe = invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal);
            var real = actual is null ? null : await actual.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            if (!probe && !stopped) { stopped = true; stopFirst.RequestStop(); }
            return real ?? new NativeMediaToolResult(0, false, probe
                ? "{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"pcm_s16le\",\"sample_rate\":\"24000\",\"channels\":1}],\"format\":{\"duration\":\"1\"}}" : "", "", TimeSpan.Zero);
        }
    }
    private sealed class AdaptiveAssemblyNative : INativeMediaTool
    {
        public Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            bool probe = invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal);
            string file = invocation.Arguments[^1];
            if (invocation.Arguments.Contains("libmp3lame", StringComparer.Ordinal))
                File.WriteAllText(file, "ID3 Synthetic inert final artifact, verified by fake native tool only.");
            string codec = file.EndsWith(".mp3", StringComparison.Ordinal) ? "mp3" : "pcm_s16le";
            string duration = string.Equals(codec, "mp3", StringComparison.Ordinal) ? "3" : "1";
            return Task.FromResult(new NativeMediaToolResult(0, false, probe
                ? JsonSerializer.Serialize(new { streams = new[] { new { codec_type = "audio", codec_name = codec, sample_rate = "24000", channels = 1 } }, format = new { duration } })
                : "", "", TimeSpan.Zero));
        }
    }
    private sealed class AdaptiveAssemblyFaultNative(INativeMediaTool inner, bool failFirst) : INativeMediaTool
    {
        public int Encodes { get; private set; }
        public Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Arguments.Contains("libmp3lame", StringComparer.Ordinal))
            {
                Encodes++;
                if (failFirst && Encodes == 1) return Task.FromResult(new NativeMediaToolResult(-1, false, "", "synthetic local failure", TimeSpan.Zero));
            }
            return inner.RunAsync(invocation, cancellationToken);
        }
    }
    private sealed class AdaptiveTransport(GoogleLongAudioRequest[] allowed, Guid[] reject, Guid? pending = null, Action? onPending = null, byte[]? pcm = null)
    {
        private readonly byte[] Audio = pcm ?? "Synthetic inert bytes for fake native verifier; no real audio claim."u8.ToArray();
        internal int Posts { get; private set; }
        internal int Calls { get; private set; }
        internal int Polls { get; private set; }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Synthetic GCS transport MD5, not authentication.")]
        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Calls++;
            if (message.Method == HttpMethod.Post)
            {
                Posts++; byte[] bytes = await message.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                var request = Assert.Single(allowed, x => x.CopyPayload().AsSpan().SequenceEqual(bytes));
                string name = "projects/123456/locations/global/operations/" + request.AttemptId.ToString("N");
                if (request.AttemptId == pending) { onPending?.Invoke(); return Json(new { name, done = false }); }
                return reject.Contains(request.AttemptId) ? Json(new
                {
                    name,
                    done = true,
                    error = new
                    {
                        code = 3,
                        message = "This request contains sentences that are too long. Consider splitting up long sentences with sentence ending punctuation e.g. periods."
                    }
                })
                    : Json(new { name, done = true });
            }
            if (string.Equals(message.RequestUri!.Host, "texttospeech.googleapis.com", StringComparison.Ordinal))
            { Polls++; return Json(new { name = "projects/123456/locations/global/operations/" + pending!.Value.ToString("N"), done = true }); }
            Assert.Equal("storage.googleapis.com", message.RequestUri!.Host);
            string objectName = Uri.UnescapeDataString(message.RequestUri.AbsolutePath.Split('/')[^1]);
            var selected = Assert.Single(allowed, x => x.OutputGcsUri.EndsWith('/' + objectName, StringComparison.Ordinal));
            if (message.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) };
            return Json(new
            {
                bucket = "synthetic-private",
                name = "output/" + selected.AttemptId.ToString("N") + ".wav",
                generation = "1",
                size = Audio.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                md5Hash = Convert.ToBase64String(MD5.HashData(Audio))
            });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}

using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleLongAudioRecoveryTests
{
    private const string SentenceError = "This request contains sentences that are too long. Consider splitting up long sentences with sentence ending punctuation e.g. periods.";
    private static GoogleLongAudioRequest Request(string text, Guid? id = null)
    {
        Guid value = id ?? Guid.NewGuid();
        return GoogleLongAudioRequest.Create(value, "123456", "global", "synthetic-ref", "gs://synthetic-private/output/" + value.ToString("N") + ".wav",
            text, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "LINEAR16");
    }
    private static GoogleLongAudioCheckpoint Rejected(GoogleLongAudioRequest request) => new(request.AttemptId, request.BindingSha256,
        request.ProjectNumber, request.Location, request.CredentialReferenceId, request.OutputGcsUri, request.PayloadSha256,
        request.PayloadBytes, GoogleLongAudioState.Failed, "projects/123456/locations/global/operations/synthetic", 200, 3, 4)
    {
        FailureKind = GoogleLongAudioFailureKind.SentenceLengthReported,
        Observation = new(Guid.NewGuid(), GoogleResponseReadState.Complete,
        GoogleLongAudioParseState.Json, GoogleErrorStatus.NotProvided, 300, GoogleExplanationRetentionState.SavedPrivately, true, false, true)
    };

    [Theory]
    [InlineData(200, 3, "sentence", GoogleLongAudioFailureKind.SentenceLengthReported)]
    [InlineData(200, 3, "other", GoogleLongAudioFailureKind.OtherInputRejected)]
    [InlineData(400, null, "sentence", GoogleLongAudioFailureKind.SentenceLengthReported)]
    [InlineData(403, null, "sentence", GoogleLongAudioFailureKind.AuthenticationOrPermission)]
    [InlineData(429, null, "sentence", GoogleLongAudioFailureKind.QuotaOrRateLimited)]
    [InlineData(503, null, "sentence", GoogleLongAudioFailureKind.TransientOrTimeout)]
    [InlineData(200, 8, "sentence", GoogleLongAudioFailureKind.QuotaOrRateLimited)]
    public void OnlyExactInvalidArgumentSentenceExplanationIsSplitEligible(int http, int? rpc, string kind, GoogleLongAudioFailureKind expected)
    {
        string message = string.Equals(kind, "sentence", StringComparison.Ordinal) ? SentenceError : "A different invalid argument.";
        byte[] body = rpc.HasValue ? JsonSerializer.SerializeToUtf8Bytes(new { name = "projects/123456/locations/global/operations/synthetic", done = true, error = new { code = rpc, message } })
            : JsonSerializer.SerializeToUtf8Bytes(new { error = new { code = http, status = "INVALID_ARGUMENT", message } });
        Assert.Equal(expected, GoogleLongAudioFailureClassifier.Classify(http, rpc, body, GoogleResponseReadState.Complete));
        Assert.NotEqual(GoogleLongAudioFailureKind.SentenceLengthReported, GoogleLongAudioFailureClassifier.Classify(http, rpc, body, GoogleResponseReadState.Failed));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"done\":true,\"error\":{\"code\":3,\"code\":3,\"message\":\"This request contains sentences that are too long.\"}}")]
    [InlineData("{\"done\":false,\"error\":{\"code\":3,\"message\":\"This request contains sentences that are too long.\"}}")]
    public void MalformedAmbiguousOrNotDoneCannotTriggerSplitting(string body) =>
        Assert.Equal(GoogleLongAudioFailureKind.Unknown, GoogleLongAudioFailureClassifier.Classify(200, 3, Encoding.UTF8.GetBytes(body), GoogleResponseReadState.Complete));

    [Fact]
    public void SplitIsLosslessStableAndPrefersParagraphsWithoutACharacterThreshold()
    {
        const string source = "First sentence. Another sentence.\n\n第二段含有表情😀和组合e\u0301，保留原文。末尾。";
        var root = Request(source); var range = new ChirpNarrationRange(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null);
        var outcome = Rejected(root);
        var a = GoogleLongAudioRecoveryPlanner.Split(source, range, root, outcome, false, 30);
        var b = GoogleLongAudioRecoveryPlanner.Split(source, range, root, outcome, false, 30);
        Assert.Equal(root.VoiceName, a[0].Request.VoiceName); Assert.Equal(root.VoiceName, a[1].Request.VoiceName);
        Assert.Equal(source, string.Concat(a.Select(x => ChirpNarrationPlanner.Slice(source, x.Range))));
        Assert.EndsWith("\n\n", ChirpNarrationPlanner.Slice(source, a[0].Range), StringComparison.Ordinal);
        Assert.Equal(a.Select(x => x.Request.BindingSha256), b.Select(x => x.Request.BindingSha256), StringComparer.Ordinal);
        Assert.All(a, p => { Assert.True(p.Request.InputUtf8Bytes < root.InputUtf8Bytes); Assert.NotEqual(root.AttemptId, p.Request.AttemptId); });
        Assert.Throws<InvalidOperationException>(() => GoogleLongAudioRecoveryPlanner.Split(source, range, root, outcome, true, 30));
        Assert.Throws<InvalidOperationException>(() => GoogleLongAudioRecoveryPlanner.Split(source, range with { ReusedReceiptSha256 = new string('a', 64) }, root, outcome, false, 30));
        Assert.Throws<InvalidOperationException>(() => GoogleLongAudioRecoveryPlanner.Split(source, range, root, outcome with { FailureKind = GoogleLongAudioFailureKind.Unknown }, false, 30));
    }

    [Fact]
    public void IndivisibleTextStopsWithoutEditingWords()
    {
        const string source = "没有标点的完整词语😀e\u0301"; var root = Request(source);
        Assert.Throws<InvalidOperationException>(() => GoogleLongAudioRecoveryPlanner.Split(source,
            new(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null), root, Rejected(root), false, 30));
    }

    [Fact]
    public void WrappedLineDoesNotOutrankAnActualSentenceEnding()
    {
        const string source = "This sentence has\r\nwrapped text, and still continues. Next complete sentence.";
        var root = Request(source); var range = new ChirpNarrationRange(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null);
        var parts = GoogleLongAudioRecoveryPlanner.Split(source, range, root, Rejected(root), false, 30);
        Assert.Equal("This sentence has\r\nwrapped text, and still continues. ", ChirpNarrationPlanner.Slice(source, parts[0].Range));
        Assert.Equal(source, string.Concat(parts.Select(x => ChirpNarrationPlanner.Slice(source, x.Range))));
    }

    [Fact]
    public async Task LongFirstSplitsOnlyFailedRangesAndRestartReusesStableAcceptedAttempts()
    {
        string source = string.Join("\n\n", Enumerable.Range(0, 16).Select(i => "Synthetic paragraph " + i + "."));
        var root = Request(source); Dictionary<Guid, GoogleLongAudioJobProgress> saved = []; int posts = 0;
        Task<GoogleLongAudioJobProgress> Execute(GoogleLongAudioRequest request, bool poll, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.False(poll);
            if (saved.TryGetValue(request.AttemptId, out var previous)) return Task.FromResult(previous);
            Assert.True(posts < 31, "Synthetic exact budget exhausted; no new request may be charged.");
            posts++; using var payload = JsonDocument.Parse(request.CopyPayload()); string text = payload.RootElement.GetProperty("input").GetProperty("text").GetString()!;
            var rejected = Rejected(request);
            bool split = text.TrimEnd().Contains("\n\n", StringComparison.Ordinal); // Fake provider ignores retained trailing separator only for its decision.
            var checkpoint = split ? rejected : rejected with { State = GoogleLongAudioState.CompletedUnverified, RpcCode = null, FailureKind = GoogleLongAudioFailureKind.Unknown };
            var identity = new GoogleLongAudioObjectIdentity(request.BindingSha256, "synthetic-private", request.AttemptId.ToString("N"), "1", 12, "AAAAAAAAAAAAAAAAAAAAAA==");
            var artifact = split ? null : new GoogleLongAudioVerifiedArtifact(identity, "synthetic-not-a-real-file", new string('a', 64), 12);
            var result = new GoogleLongAudioJobProgress(checkpoint, artifact); saved.Add(request.AttemptId, result); return Task.FromResult(result);
        }
        var runner = new GoogleLongAudioRecoveryRunner(Execute, (_, _) => Task.FromResult(new GoogleLongAudioRecoveryAllowance(31 - saved.Count, false, false)), 30, TimeProvider.System);
        var first = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.True(first.CoverageComplete); Assert.Equal(16, first.Outputs.Count); Assert.Equal(31, posts); // Four split levels; no arbitrary two-level guard.
        Assert.Equal(source, string.Concat(first.Outputs.Select(x => ChirpNarrationPlanner.Slice(source, x.Range))));
        var resumed = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.True(resumed.CoverageComplete); Assert.Equal(31, posts); Assert.Equal(first.Outputs, resumed.Outputs);
    }

    [Fact]
    public void LongWhitespaceAndClosingRunsDoNotCauseRepeatedScanning()
    {
        string source = "Alpha" + new string('\n', 120_000) + new string('”', 120_000) + "Beta。" + new string('\n', 120_000);
        var root = Request(source); var range = new ChirpNarrationRange(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var parts = GoogleLongAudioRecoveryPlanner.Split(source, range, root, Rejected(root), false, 30);
        Assert.Equal(source, string.Concat(parts.Select(x => ChirpNarrationPlanner.Slice(source, x.Range))));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "Boundary scanning regressed on one long whitespace/closing run.");
    }

    [Fact]
    public async Task WholeLongAudioSuccessUsesOneRequestAndNeverConsultsSplitAllowance()
    {
        var root = Request("Whole synthetic article.\n\nSecond paragraph."); int calls = 0;
        var checkpoint = Rejected(root) with { State = GoogleLongAudioState.CompletedUnverified, RpcCode = null, FailureKind = GoogleLongAudioFailureKind.Unknown };
        var artifact = new GoogleLongAudioVerifiedArtifact(new(root.BindingSha256, "synthetic-private", root.AttemptId.ToString("N"), "1", 12,
            "AAAAAAAAAAAAAAAAAAAAAA=="), "synthetic-no-real-audio", new string('a', 64), 12);
        var runner = new GoogleLongAudioRecoveryRunner((_, poll, _) => { calls++; Assert.False(poll); return Task.FromResult(new GoogleLongAudioJobProgress(checkpoint, artifact)); },
            (_, _) => throw new InvalidOperationException("Successful audio must not be split."), 30, TimeProvider.System);
        var result = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.True(result.CoverageComplete); Assert.Single(result.Outputs); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnadmittedChildStopsBeforeAProviderCallAndNeverTriesAnotherRoute()
    {
        var root = Request("First paragraph.\n\nSecond paragraph."); int providerPosts = 0, childrenReviewed = 0;
        var runner = new GoogleLongAudioRecoveryRunner((request, _, _) =>
        {
            if (request.AttemptId != root.AttemptId) { childrenReviewed++; throw new InvalidOperationException("Synthetic exact admission denied before dispatch."); }
            providerPosts++; return Task.FromResult(new GoogleLongAudioJobProgress(Rejected(root), null));
        }, (_, _) => Task.FromResult(new GoogleLongAudioRecoveryAllowance(0, false, false)), 30, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
        Assert.Equal(1, providerPosts); Assert.Equal(1, childrenReviewed);
    }

    [Theory]
    [InlineData(GoogleLongAudioFailureKind.AuthenticationOrPermission)]
    [InlineData(GoogleLongAudioFailureKind.QuotaOrRateLimited)]
    [InlineData(GoogleLongAudioFailureKind.TransientOrTimeout)]
    [InlineData(GoogleLongAudioFailureKind.Unknown)]
    public async Task UnrelatedFailuresNeverCreateChildren(GoogleLongAudioFailureKind kind)
    {
        var root = Request("Synthetic first.\n\nSynthetic second."); int attempts = 0, allowances = 0;
        var runner = new GoogleLongAudioRecoveryRunner((request, _, _) => { attempts++; return Task.FromResult(new GoogleLongAudioJobProgress(Rejected(request) with { FailureKind = kind }, null)); },
            (_, _) => { allowances++; return Task.FromResult(new GoogleLongAudioRecoveryAllowance(2000, false, false)); }, 30, TimeProvider.System);
        var result = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.False(result.CoverageComplete); Assert.Empty(result.Outputs); Assert.Equal(1, attempts); Assert.Equal(0, allowances);
    }
}

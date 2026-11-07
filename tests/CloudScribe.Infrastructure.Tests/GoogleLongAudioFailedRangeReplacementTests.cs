using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleLongAudioFailedRangeReplacementTests
{
    private static GoogleLongAudioRequest Request(string text, Guid? id = null)
    {
        Guid attempt = id ?? Guid.NewGuid();
        return GoogleLongAudioRequest.Create(attempt, "123456", "global", "synthetic-ref",
            "gs://synthetic-private/output/" + attempt.ToString("N") + ".wav", text,
            "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "LINEAR16");
    }

    private static GoogleLongAudioCheckpoint Failed(GoogleLongAudioRequest request) => new(request.AttemptId,
        request.BindingSha256, request.ProjectNumber, request.Location, request.CredentialReferenceId,
        request.OutputGcsUri, request.PayloadSha256, request.PayloadBytes, GoogleLongAudioState.Failed,
        "projects/123456/locations/global/operations/synthetic", 200, 13, 4)
    {
        FailureKind = GoogleLongAudioFailureKind.TransientOrTimeout,
        Observation = new(Guid.NewGuid(), GoogleResponseReadState.Complete, GoogleLongAudioParseState.Json,
            GoogleErrorStatus.NotProvided, 300, GoogleExplanationRetentionState.SavedPrivately, true, false, false)
    };

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-checkpoint")]
    [InlineData("wrong-range")]
    [InlineData("changed-source")]
    [InlineData("accepted-output")]
    [InlineData("pending")]
    [InlineData("different-error")]
    [InlineData("missing-capture")]
    [InlineData("changed-binding")]
    [InlineData("zero-budget")]
    [InlineData("failed-root")]
    public void ExactReplacementRequiresDefinitivePinnedFailureAndUnchangedSource(string fault)
    {
        const string text = "Second unchanged sentence.";
        const string source = "First sentence. " + text;
        var root = Request(source); var failed = Request(text);
        var range = new ChirpNarrationRange(16, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(text), null);
        var checkpoint = Failed(failed); var replacement = Request(text); string pin = new('a', 64);
        var approval = new GoogleLongAudioFailedRangeReplacement(root.BindingSha256, failed.AttemptId,
            failed.BindingSha256, pin, range.StartScalar, range.EndScalarExclusive, range.TextSha256, replacement.AttemptId,
            replacement.OutputGcsUri, replacement.BindingSha256, 1);
        if (fault is "wrong-checkpoint") pin = new('b', 64);
        if (fault is "wrong-range") approval = approval with { StartScalar = 1 };
        if (fault is "changed-source") approval = approval with { SourceSha256 = new('c', 64) };
        if (fault is "pending") checkpoint = checkpoint with { State = GoogleLongAudioState.Pollable };
        if (fault is "different-error") checkpoint = checkpoint with { RpcCode = 3, FailureKind = GoogleLongAudioFailureKind.SentenceLengthReported };
        if (fault is "missing-capture") checkpoint = checkpoint with { Observation = null };
        if (fault is "changed-binding") approval = approval with { ReplacementBindingSha256 = new('d', 64) };
        if (fault is "zero-budget") approval = approval with { MaximumUsdCents = 0 };
        if (fault is "failed-root") root = failed;
        GoogleLongAudioRequest Act() => GoogleLongAudioFailedRangeReplacementPlanner.Create(source, range, root, failed,
            checkpoint, pin, approval, fault is "accepted-output", 30);
        if (fault is "valid")
        {
            var actual = Act(); Assert.Equal(replacement.BindingSha256, actual.BindingSha256);
            using var wire = System.Text.Json.JsonDocument.Parse(actual.CopyPayload());
            Assert.Equal(text, wire.RootElement.GetProperty("input").GetProperty("text").GetString());
            Assert.Equal(root.VoiceName, actual.VoiceName); Assert.NotEqual(root.AttemptId, actual.AttemptId);
        }
        else Assert.Throws<InvalidDataException>(Act);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementFailureStopsWithoutSplitOrAnotherReplacement(bool replacementFails)
    {
        const string source = "One unchanged sentence."; var root = Request(source); var replacement = Request(source);
        Dictionary<Guid, GoogleLongAudioJobProgress> journal = []; int posts = 0, replacementOffers = 0;
        Task<GoogleLongAudioJobProgress> Execute(GoogleLongAudioRequest request, bool poll, CancellationToken token)
        {
            Assert.False(poll); token.ThrowIfCancellationRequested();
            if (journal.TryGetValue(request.AttemptId, out var prior)) return Task.FromResult(prior);
            posts++; var checkpoint = Failed(request); GoogleLongAudioVerifiedArtifact? artifact = null;
            if (request.AttemptId == replacement.AttemptId && !replacementFails)
            {
                checkpoint = checkpoint with { State = GoogleLongAudioState.CompletedUnverified, RpcCode = null, FailureKind = GoogleLongAudioFailureKind.Unknown };
                artifact = new(new(request.BindingSha256, "synthetic-private", request.AttemptId.ToString("N"), "1", 12, "AAAAAAAAAAAAAAAAAAAAAA=="),
                    "synthetic-no-real-file", new string('a', 64), 12);
            }
            var result = new GoogleLongAudioJobProgress(checkpoint, artifact); journal.Add(request.AttemptId, result); return Task.FromResult(result);
        }
        var runner = new GoogleLongAudioRecoveryRunner(Execute,
            (_, _) => throw new InvalidOperationException("A separately approved one-time replacement must never enter automatic splitting."),
            30, TimeProvider.System, replaceFailedRange: (_, _, request, _) =>
            { replacementOffers++; Assert.Equal(root.AttemptId, request.AttemptId); return replacement; },
            isExplicitReplacement: request => request.AttemptId == replacement.AttemptId);
        var first = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(!replacementFails, first.CoverageComplete); Assert.Equal(2, posts); Assert.Equal(1, replacementOffers);
        var resumed = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(first.CoverageComplete, resumed.CoverageComplete); Assert.Equal(2, posts);
        Assert.Equal(2, journal.Count); Assert.Equal(13, journal[root.AttemptId].Checkpoint.RpcCode);
    }
}

using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ChirpArticleAudioCoverageTests
{
    [Fact]
    public void OrderedInternalRequestsAndExistingAcceptedAudioAreEachUsedOnce()
    {
        var (plan, segments, reused) = Fixture();
        var result = ChirpArticleAudioCoverage.RequireComplete(plan, segments.Reverse().ToArray(), reused);
        Assert.Equal(plan.Parts.Count, result.Count);
        Assert.Same(reused[0].Audio, result[plan.Parts.Single(x => x.Range.ReusedReceiptSha256 is not null).CoverageOrdinal - 1]);
        Assert.All(segments, x => Assert.Equal(plan.DocumentId, x.Audio.DocumentId));
        Assert.NotEqual(plan.DocumentId, reused[0].Audio.DocumentId);
    }

    [Fact]
    public void MissingDuplicateStaleOrMismatchedAudioBlocksExport()
    {
        var (plan, segments, reused) = Fixture();
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, segments[..^1], reused));
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, segments.Append(segments[0]).ToArray(), reused));
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, segments,
            [reused[0] with { ParentRevisionId = Guid.NewGuid() }]));
        var altered = segments.ToArray();
        altered[0] = altered[0] with { PlanSha256 = new string('f', 64) };
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, altered, reused));
        altered[0] = segments[0] with { Audio = segments[0].Audio with { LocalAttemptId = segments[1].Audio.LocalAttemptId } };
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, altered, reused));
        altered[0] = segments[0] with { Audio = segments[0].Audio with { VoiceName = "different" } };
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, altered, reused));
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan with { SourceScalars = plan.SourceScalars + 1 }, segments, reused));
    }

    [Fact]
    public void MissingNativeIdentitiesAndInvalidReuseDigestCannotStandInForHistoricalProof()
    {
        var (plan, segments, reused) = Fixture();
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan with { DocumentId = Guid.Empty }, segments, reused));
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, segments,
            [reused[0] with { Audio = reused[0].Audio with { DocumentId = Guid.Empty } }]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(plan, segments,
            [reused[0] with { Audio = reused[0].Audio with { RevisionId = Guid.Empty } }]));
        var badRange = reused[0].Range with { ReusedReceiptSha256 = "bad" };
        var malformed = plan with { Parts = plan.Parts.Select(x => x.Range.ReusedReceiptSha256 is null ? x : x with { Range = badRange }).ToArray() };
        Assert.Throws<InvalidOperationException>(() => ChirpArticleAudioCoverage.RequireComplete(malformed, segments,
            [reused[0] with { ReceiptSha256 = "bad", Range = badRange }]));
    }

    private static (ChirpArticlePlanProposal Plan, ChirpSegmentAcceptedAudio[] Segments, ChirpReusedArticleAudio[] Reused) Fixture()
    {
        string source = string.Concat(Enumerable.Repeat("文，", 400));
        var document = new DocumentSnapshot(Guid.NewGuid(), "Synthetic", source, 0, 0, DocumentStatus.Active, false, Guid.NewGuid(), null, null, 0);
        var reuse = new ChirpNarrationReuse(400, 600, ArticleAudioManifest.HashText(source[400..600]), new string('a', 64));
        var plan = ChirpArticlePlanCompiler.Compile(document, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", [reuse]);
        var segments = plan.Parts.Where(x => x.Range.ReusedReceiptSha256 is null)
            .Select(x => new ChirpSegmentAcceptedAudio(plan.PlanSha256, x.CoverageOrdinal, x.Range, Audio(plan.DocumentId, plan.RevisionId, x))).ToArray();
        var retained = plan.Parts.Single(x => x.Range.ReusedReceiptSha256 is not null);
        ChirpReusedArticleAudio[] reused = [new(plan.DocumentId, plan.RevisionId, reuse.ReceiptSha256, retained.Range,
            Audio(Guid.NewGuid(), Guid.NewGuid(), retained))];
        return (plan, segments, reused);
    }

    private static ArticleAcceptedAudio Audio(Guid document, Guid revision, ChirpArticlePlanPart part) => new(document, revision,
        part.Range.TextSha256, "synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId, "synthetic-" + Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"), part.PayloadSha256 ?? new string('b', 64), "synthetic-" + Guid.NewGuid().ToString("N") + ".mp3", 100,
        new string('c', 64), "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN");
}

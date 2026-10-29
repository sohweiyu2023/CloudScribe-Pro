using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ChirpArticlePlanCompilerTests
{
    private const string Voice = "cmn-CN-Chirp3-HD-Alnilam";

    [Fact]
    public void ExactSourceRevisionCoverageVoiceAndPayloadsBindDeterministicProposal()
    {
        var document = Source();
        var plan = ChirpArticlePlanCompiler.Compile(document, "cmn-CN", Voice);
        Assert.Equal(plan.PlanSha256, ChirpArticlePlanCompiler.Compile(document, "cmn-CN", Voice).PlanSha256);
        Assert.All(plan.Parts, x => { Assert.NotNull(x.PayloadSha256); Assert.InRange(x.PayloadBytes, 1, 5000); });
        ChirpArticlePlanCompiler.RequireCurrent(plan, document);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document with { Id = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document with { CurrentRevisionId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document with { Text = document.Text.Replace('文', '字') }));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan with { VoiceName = "cmn-CN-Chirp3-HD-Achernar" }, document));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan with { Parts = plan.Parts.Reverse().ToArray() }, document));
    }

    [Fact]
    public void ReuseHasNoCompiledRequestAndChangesProposalIdentity()
    {
        var document = Source();
        var reuse = new ChirpNarrationReuse(400, 800, ArticleAudioManifest.HashText(document.Text[400..800]), new string('a', 64));
        var plan = ChirpArticlePlanCompiler.Compile(document, "cmn-CN", Voice, [reuse]);
        var kept = Assert.Single(plan.Parts, x => x.Range.ReusedReceiptSha256 is not null);
        Assert.Null(kept.PayloadSha256);
        Assert.Equal(0, kept.PayloadBytes);
        Assert.NotEqual(ChirpArticlePlanCompiler.Compile(document, "cmn-CN", Voice).PlanSha256, plan.PlanSha256, StringComparer.Ordinal);
        ChirpArticlePlanCompiler.RequireCurrent(plan, document, [reuse]);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document, [reuse with { ReceiptSha256 = new string('b', 64) }]));
    }

    [Fact]
    public void MissingNativeRevisionOrNonChirpVoiceCannotProduceProposal()
    {
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(Source() with { CurrentRevisionId = null }, "cmn-CN", Voice));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(Source(), "cmn-CN", "cmn-CN-Wavenet-A"));
    }

    private static DocumentSnapshot Source() => new(Guid.NewGuid(), "Synthetic", string.Concat(Enumerable.Repeat("文，", 600)),
        0, 0, DocumentStatus.Active, false, Guid.NewGuid(), null, null, 0);
}

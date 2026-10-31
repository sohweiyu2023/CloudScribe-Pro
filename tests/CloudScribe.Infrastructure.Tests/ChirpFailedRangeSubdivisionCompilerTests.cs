using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ChirpFailedRangeSubdivisionCompilerTests
{
    private const string Voice = "cmn-CN-Chirp3-HD-Alnilam";
    private static DocumentSnapshot Source() => new(Guid.NewGuid(), "Synthetic clauses",
        string.Concat(Enumerable.Repeat("这是合成测试，保留全部文字。\r\n", 40)), 0, 0, DocumentStatus.Active, false, Guid.NewGuid(), null, null, 0);

    [Fact]
    public void ExistingCompilerBindsExactSmallerPayloadsWithoutChangingOriginalPlan()
    {
        var doc = Source(); var original = ChirpArticlePlanCompiler.Compile(doc, "cmn-CN", Voice);
        string originalHash = ChirpPolicyValidation.Hash(original);
        var proposal = ChirpFailedRangeSubdivisionCompiler.Compile(doc, original, 1);
        Assert.True(proposal.Parts.Count > 1);
        Assert.Equal(original.PlanSha256, proposal.ParentPlanSha256);
        Assert.Equal(original.Parts[0].Range, proposal.ParentRange);
        Assert.Equal(originalHash, ChirpPolicyValidation.Hash(original));
        Assert.Equal(ChirpNarrationPlanner.Slice(doc.Text, proposal.ParentRange), string.Concat(proposal.Parts.Select(x => ChirpNarrationPlanner.Slice(doc.Text, x.Range))));
        foreach (var part in proposal.Parts)
        {
            var compiled = GoogleSpeechPlanCompiler.Compile(new SpeechPlan("cmn-CN", [new SpeechText(ChirpNarrationPlanner.Slice(doc.Text, part.Range))], ChirpClauseSubdivision.Version), new("cmn-CN", Voice, "MP3", 5_000));
            Assert.Equal(compiled.PayloadSha256, part.PayloadSha256); Assert.Equal(compiled.Payload.Length, part.PayloadBytes);
        }
        ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal, doc, original);
    }

    [Fact]
    public void TamperedVoicePayloadRangeOrderSourceAndParentCannotRevalidate()
    {
        var doc = Source(); var original = ChirpArticlePlanCompiler.Compile(doc, "cmn-CN", Voice);
        var proposal = ChirpFailedRangeSubdivisionCompiler.Compile(doc, original, 1);
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal with { VoiceName = "cmn-CN-Chirp3-HD-Achernar" }, doc, original));
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal with { Parts = proposal.Parts.Reverse().ToArray() }, doc, original));
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal with { ParentPayloadSha256 = new string('b', 64) }, doc, original));
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal, doc with { Text = doc.Text + "改" }, original));
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal, doc with { CurrentRevisionId = Guid.NewGuid() }, original));
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.RequireCurrent(proposal, doc, original with { PlanSha256 = new string('b', 64) }));
    }

    [Fact]
    public void AcceptedOriginalRangeCannotBecomeSubdivisionRequests()
    {
        var doc = Source(); string retained = doc.Text[..16];
        var reuse = new ChirpNarrationReuse(0, retained.EnumerateRunes().Count(), ArticleAudioManifest.HashText(retained), new string('a', 64));
        var plan = ChirpArticlePlanCompiler.Compile(doc, "cmn-CN", Voice, [reuse]);
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.Compile(doc, plan, 1, [reuse]));
    }
}

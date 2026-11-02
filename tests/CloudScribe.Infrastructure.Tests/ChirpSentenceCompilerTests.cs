using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ChirpSentenceCompilerTests
{
    private static DocumentSnapshot Source() => new(Guid.NewGuid(), "Synthetic", string.Concat(Enumerable.Repeat("clause, whole sentence\u3002\n", 60)),
        0, 0, DocumentStatus.Active, false, Guid.NewGuid(), null, null, 0);

    [Fact]
    public void FreshSentencePlanAndOneBoundedSuccessorRecompileExactly()
    {
        var document = Source(); var parent = ChirpArticlePlanCompiler.Compile(document, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var retained = parent.Parts[0].Range;
        var reuse = new ChirpNarrationReuse(retained.StartScalar, retained.EndScalarExclusive, retained.TextSha256, new string('a', 64));
        var proposal = ChirpArticlePlanCompiler.CompileSentenceSuccessor(document, parent, [reuse]);
        ChirpArticlePlanCompiler.RequireCurrent(proposal, document, [reuse]);
        Assert.Equal(parent, proposal.SentenceSuccessor!.Parent);
        Assert.Single(proposal.Parts, x => x.Range.ReusedReceiptSha256 is not null);
        Assert.Equal(proposal, ChirpRefinedPlanSelection.RequireUniqueLeaf([proposal, parent]));
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([proposal]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileSentenceSuccessor(document, proposal, [reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(proposal, document with { Text = document.Text + "changed" }, [reuse]));
        var fresh = ChirpArticlePlanCompiler.CompileSentences(document, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        ChirpArticlePlanCompiler.RequireCurrent(fresh, document);
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([fresh, parent]));
        Assert.DoesNotContain("SentenceSuccessor", JsonSerializer.Serialize(parent), StringComparison.Ordinal);
    }

    [Fact]
    public void RefinedParentCanHaveOneSentenceChildButNoSiblingsOrMissingRoot()
    {
        var doc = Source(); var root = ChirpArticlePlanCompiler.Compile(doc, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var metadata = new ChirpFailedPlanRefinement(root, 1, Guid.NewGuid(), new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("N"));
        var refined = ChirpArticlePlanCompiler.CompileRefined(doc, metadata, []);
        var child = ChirpArticlePlanCompiler.CompileSentenceSuccessor(doc, refined, []);
        ChirpArticlePlanCompiler.RequireCurrent(child, doc);
        Assert.Equal(child, ChirpRefinedPlanSelection.RequireUniqueLeaf([child, root, refined]));
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([child, refined]));
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([child, root, refined, child]));
    }
}

using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ChirpRefinedPlanSelectionTests
{
    private static DocumentSnapshot Source() => new(Guid.NewGuid(), "Synthetic", string.Concat(Enumerable.Repeat("甲乙，丙丁。\n", 90)),
        0, 0, DocumentStatus.Active, false, Guid.NewGuid(), null, null, 0);

    [Fact]
    public void RefinedPlanKeepsAllOtherPayloadsAndReusesExactAcceptedRange()
    {
        var doc = Source(); var original = ChirpArticlePlanCompiler.Compile(doc, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var accepted = original.Parts[1].Range;
        var reuse = new ChirpNarrationReuse(accepted.StartScalar, accepted.EndScalarExclusive, accepted.TextSha256, new string('d', 64));
        var refinement = new ChirpFailedPlanRefinement(original, 1, Guid.NewGuid(), new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("N"));
        var refined = ChirpArticlePlanCompiler.CompileRefined(doc, refinement, [reuse]);
        ChirpArticlePlanCompiler.RequireCurrent(refined, doc, [reuse]);
        Assert.NotEqual(original.PlanSha256, refined.PlanSha256, StringComparer.Ordinal);
        Assert.Equal(original, refined.Refinement!.Parent);
        Assert.Single(refined.Parts, x => string.Equals(x.Range.ReusedReceiptSha256, reuse.ReceiptSha256, StringComparison.Ordinal));
        foreach (var old in original.Parts.Skip(2))
        {
            var kept = Assert.Single(refined.Parts, x => x.Range == old.Range);
            Assert.Equal(old.PayloadSha256, kept.PayloadSha256); Assert.Equal(old.PayloadBytes, kept.PayloadBytes);
        }
        Assert.Equal(doc.Text, string.Concat(refined.Parts.Select(x => ChirpNarrationPlanner.Slice(doc.Text, x.Range))));
        Assert.Equal(refined, ChirpRefinedPlanSelection.RequireUniqueLeaf([refined, original]));
        Assert.Equal(refined, ChirpRefinedPlanSelection.RequireUniqueLeaf([original, refined]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileRefined(doc, refinement, [new(0, 1, new string('a', 64), new string('b', 64))]));
    }

    [Fact]
    public void OrphanSiblingsNestedAndChangedParentAreRejected()
    {
        var doc = Source(); var parent = ChirpArticlePlanCompiler.Compile(doc, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var metadata = new ChirpFailedPlanRefinement(parent, 1, Guid.NewGuid(), new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("N"));
        var child = ChirpArticlePlanCompiler.CompileRefined(doc, metadata, []);
        var sibling = ChirpArticlePlanCompiler.CompileRefined(doc, metadata with { FailedAuthorizationId = Guid.NewGuid() }, []);
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([child]));
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([parent, child, sibling]));
        Assert.Throws<InvalidOperationException>(() => ChirpRefinedPlanSelection.RequireUniqueLeaf([child, sibling]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileRefined(doc, metadata with { Parent = child }, []));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(child, doc with { Text = doc.Text + "改" }));
    }

    [Fact]
    public void LegacySerializedPlanHasNoNewNullFields()
    {
        var plan = ChirpArticlePlanCompiler.Compile(Source(), "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        Assert.DoesNotContain("Refinement", JsonSerializer.Serialize(plan), StringComparison.Ordinal);
        ChirpArticlePlanCompiler.RequireIntegrity(plan);
    }
}

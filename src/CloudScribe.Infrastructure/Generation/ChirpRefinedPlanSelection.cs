using System.Text.Json;
using CloudScribe.Application.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>One immutable predecessor and one exact refined child; never newest-by-time or caller preference.</summary>
public static class ChirpRefinedPlanSelection
{
    public static ChirpArticlePlanProposal RequireUniqueLeaf(IReadOnlyList<ChirpArticlePlanProposal> plans)
    {
        try { return RequireUniqueLeafCore(plans); }
        catch (InvalidOperationException error) { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.RefinementPlanAmbiguous); }
    }

    private static ChirpArticlePlanProposal RequireUniqueLeafCore(IReadOnlyList<ChirpArticlePlanProposal> plans)
    {
        if (plans.Count is < 1 or > 2) throw new InvalidOperationException("A unique bounded article plan is required.");
        foreach (var plan in plans) ChirpArticlePlanCompiler.RequireIntegrity(plan);
        if (plans.Count == 1)
        {
            if (plans[0].Refinement is not null) throw new InvalidOperationException("Refinement predecessor is absent.");
            return plans[0];
        }
        var roots = plans.Where(x => x.Refinement is null).ToArray(); var children = plans.Where(x => x.Refinement is not null).ToArray();
        if (roots.Length != 1 || children.Length != 1 || roots[0].DocumentId != children[0].DocumentId
            || roots[0].RevisionId != children[0].RevisionId || !string.Equals(roots[0].SourceSha256, children[0].SourceSha256
, StringComparison.Ordinal) || !JsonSerializer.SerializeToUtf8Bytes(roots[0]).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(children[0].Refinement!.Parent)))
            throw new InvalidOperationException("Article refinements are ambiguous, orphaned or changed; no export was started.");
        return children[0];
    }
}

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
        int articleFormats = plans.Count(p => p.InputFormat is not null);
        int failedFormats = plans.Count(p => p.Refinement?.SentenceMarkers is not null);
        if (plans.Count is < 1 or > 5 || articleFormats > 1
            || plans.Count == 4 && failedFormats != 1 && articleFormats != 1
            || plans.Count == 5 && (failedFormats != 1 || articleFormats != 1)
            || plans.Any(p => p.InputFormat is not null && (p.InputFormat.Version != 1 || p.Refinement is not null
                || !string.Equals(p.PlannerVersion, ChirpArticlePlanCompiler.SentenceMarkedArticleVersion, StringComparison.Ordinal))))
            throw new InvalidOperationException("A unique bounded article plan is required.");
        foreach (var plan in plans) ChirpArticlePlanCompiler.RequireIntegrity(plan);
        ChirpArticlePlanProposal? Parent(ChirpArticlePlanProposal p)
        {
            if (p.Refinement is not null && p.SentenceSuccessor is not null) throw new InvalidOperationException("Competing article predecessor mechanisms.");
            return p.SentenceSuccessor?.Parent ?? p.Refinement?.Parent;
        }
        var roots = plans.Where(x => Parent(x) is null).ToArray();
        var leaves = plans.Where(x => !plans.Any(y => string.Equals(Parent(y)?.PlanSha256, x.PlanSha256, StringComparison.Ordinal))).ToArray();
        if (plans.Any(p => p.InputFormat is not null && !leaves.Contains(p)))
            throw new InvalidOperationException("An article input-format transition cannot have another successor.");
        if (roots.Length != 1 || leaves.Length != 1 || plans.Select(x => x.PlanSha256).Distinct(StringComparer.Ordinal).Count() != plans.Count)
            throw new InvalidOperationException("Article successors are ambiguous, orphaned or duplicated.");
        HashSet<string> visited = []; var cursor = leaves[0];
        while (true)
        {
            if (!visited.Add(cursor.PlanSha256)) throw new InvalidOperationException("Article successor cycle detected.");
            var predecessor = Parent(cursor); if (predecessor is null) break;
            var exact = plans.SingleOrDefault(x => string.Equals(x.PlanSha256, predecessor.PlanSha256, StringComparison.Ordinal));
            if (exact is null || exact.DocumentId != cursor.DocumentId || exact.RevisionId != cursor.RevisionId
                || !string.Equals(exact.SourceSha256, cursor.SourceSha256, StringComparison.Ordinal)
                || !JsonSerializer.SerializeToUtf8Bytes(exact).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(predecessor)))
                throw new InvalidOperationException("An immutable article predecessor changed.");
            cursor = exact;
        }
        if (visited.Count != plans.Count) throw new InvalidOperationException("Article predecessor chain is incomplete.");
        return leaves[0];
    }
}

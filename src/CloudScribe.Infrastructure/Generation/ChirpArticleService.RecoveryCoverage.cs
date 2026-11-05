using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<IReadOnlyList<ChirpRecoveredArticleCoverage>> RecoveredCoverageAsync(SqliteConnection connection,
        SqliteTransaction transaction, ChirpStoredArticle stored, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT m.AuthorizationId FROM chirp_article_split_mappings m JOIN chirp_article_split_attempts a
                ON a.AuthorizationId=m.AuthorizationId WHERE a.PlanSha256=$plan ORDER BY m.AuthorizationId LIMIT 10001;
            """, ("$plan", stored.Article.Proposal.PlanSha256));
        List<Guid> ids = [];
        using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await row.ReadAsync(token).ConfigureAwait(false))
            {
                if (ids.Count == 10000) throw new InvalidDataException("Recovery coverage exceeds its inspection bound.");
                ids.Add(Guid.ParseExact(row.GetString(0), "N"));
            }
        List<ChirpArticleRecoveryChild> children = [];
        foreach (var id in ids) children.Add(await ReadRecoveryChildAsync(connection, transaction, id, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Recovery ownership mapping is absent."));
        List<ChirpRecoveredArticleCoverage> completed = [];
        foreach (var group in children.GroupBy(x => x.CoverageOrdinal))
        {
            var roots = group.Select(x => x.RootAuthorizationId).Distinct().ToArray();
            var part = stored.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == group.Key && x.Range.ReusedReceiptSha256 is null);
            if (roots.Length != 1 || group.Count() > 2L * (part.Range.EndScalarExclusive - part.Range.StartScalar - 1))
                throw new InvalidOperationException("Recovery has duplicate roots or exceeds its strictly shrinking source tree.");
            HashSet<Guid> visited = [];
            var leaves = await RecoveryLeavesAsync(connection, transaction, roots[0], part.Range, 0, group.ToArray(), visited, token).ConfigureAwait(false);
            if (leaves is not null && !visited.SetEquals(group.Select(x => x.AuthorizationId)))
                throw new InvalidOperationException("Recovery contains orphaned child evidence.");
            if (leaves is not null) completed.Add(new(stored.Article.Proposal.PlanSha256, group.Key, part.Range, leaves));
        }
        return completed.AsReadOnly();
    }

    private async Task<IReadOnlyList<ChirpSegmentAcceptedAudio>?> RecoveryLeavesAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid parent, ChirpNarrationRange range, int depth,
        IReadOnlyList<ChirpArticleRecoveryChild> tree, HashSet<Guid> visited, CancellationToken token)
    {
        if (depth > tree.Count) throw new InvalidOperationException("Recovery depth cannot exceed its finite owned tree.");
        var node = tree.SingleOrDefault(x => x.AuthorizationId == parent);
        var children = tree.Where(x => x.ParentAuthorizationId == parent).OrderBy(x => x.ChildSlot).ToArray();
        if (node is not null)
        {
            if (!visited.Add(node.AuthorizationId)) throw new InvalidOperationException("Recovery contains duplicated or cyclic child evidence.");
            _ = await OutputPartAsync(connection, transaction,
                (await StoredAsync(connection, transaction, node.PlanSha256, token).ConfigureAwait(false)).Article.Proposal,
                node.AuthorizationId, node.CoverageOrdinal, token).ConfigureAwait(false);
            var receipt = await ReadRecoveryReceiptAsync(connection, transaction, node, token).ConfigureAwait(false);
            if (receipt is not null)
            {
                if (children.Length != 0) throw new InvalidOperationException("Accepted child coverage cannot have replacements.");
                return [receipt];
            }
        }
        if (children.Length == 0) return null;
        if (children.Length != 2 || children[0].ChildSlot != 1 || children[1].ChildSlot != 2
            || children.Any(x => x.Depth != depth + 1) || children[0].Range.StartScalar != range.StartScalar
            || children[0].Range.EndScalarExclusive != children[1].Range.StartScalar || children[1].Range.EndScalarExclusive != range.EndScalarExclusive)
            return null;
        var left = await RecoveryLeavesAsync(connection, transaction, children[0].AuthorizationId, children[0].Range, depth + 1, tree, visited, token).ConfigureAwait(false);
        var right = await RecoveryLeavesAsync(connection, transaction, children[1].AuthorizationId, children[1].Range, depth + 1, tree, visited, token).ConfigureAwait(false);
        return left is null || right is null ? null : left.Concat(right).ToArray();
    }
}

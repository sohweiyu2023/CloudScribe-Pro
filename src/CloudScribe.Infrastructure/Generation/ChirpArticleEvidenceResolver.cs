using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Transaction-owned native source/history verification. Policy inputs cannot replace current database evidence.</summary>
internal sealed partial class ChirpArticleEvidenceResolver(AppPaths paths)
{
    internal async Task<(DocumentSnapshot Document, AdmittedChirpArticle Article)> ResolveAsync(SqliteConnection connection,
        SqliteTransaction transaction, ChirpArticleAdmissionPolicy policy, Guid documentId, RecoveryFinancialSnapshot financial, CancellationToken token,
        bool requirePersistedLegacy = false)
    {
        var matches = policy.Articles.Where(x => x.Proposal.DocumentId == documentId).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The saved article has no unique independently admitted internal plan.");
        var result = await ResolveSnapshotAsync(connection, transaction, matches[0], policy.AccountId, policy.ProjectId, financial, token).ConfigureAwait(false);
        await RequireMissingLegacyAsync(connection, transaction, matches[0], policy, financial, token).ConfigureAwait(false);
        await RequireLegacyAsync(connection, transaction, matches[0], policy.AccountId, policy.ProjectId, financial,
            (policy.LegacyAssociations ?? []).Where(x => string.Equals(x.PlanSha256, matches[0].Proposal.PlanSha256, StringComparison.Ordinal)).ToArray(), requirePersistedLegacy, token).ConfigureAwait(false);
        return result;
    }

    // Local readback/export uses immutable stored provenance, never treating it as current paid authority.
    internal async Task<(DocumentSnapshot Document, AdmittedChirpArticle Article)> ResolveSnapshotAsync(SqliteConnection connection,
        SqliteTransaction transaction, AdmittedChirpArticle admitted, string accountId, string projectId, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        Guid documentId = admitted.Proposal.DocumentId;
        var document = await ReadDocumentAsync(connection, transaction, documentId, token).ConfigureAwait(false);
        var actualHistory = await ReadHistoryAsync(connection, transaction, document, admitted, accountId, projectId, financial, token).ConfigureAwait(false);
        var reuse = admitted.ReusedAudio.Select(x => new ChirpNarrationReuse(x.Range.StartScalar, x.Range.EndScalarExclusive,
            x.Range.TextSha256, x.ReceiptSha256)).ToArray();
        ChirpArticlePlanCompiler.RequireCurrent(admitted.Proposal, document, reuse);
        var mapped = ChirpPlanLineageMapper.Map(document.Text, admitted.Proposal.Parts.Select(x => x.Range).ToArray(), actualHistory);
        ChirpPlanLineageMapper.RequireExact(admitted.Lineage, mapped);
        foreach (var retained in admitted.ReusedAudio)
            await RequireReusedAsync(connection, transaction, document, admitted, retained, financial, token).ConfigureAwait(false);
        // Historical readback/export can inspect committed supplements without granting new paid authority.
        var legacy = await StoredLegacyAsync(connection, transaction, admitted.Proposal.PlanSha256, token).ConfigureAwait(false);
        await RequireLegacyAsync(connection, transaction, admitted, accountId, projectId, financial, legacy, true, token).ConfigureAwait(false);
        return (document, admitted);
    }

    internal static async Task<DocumentSnapshot> ReadDocumentAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT d.Title,d.DraftText,d.CurrentRevisionId,d.Status,r.ContentText,r.ContentSha256
            FROM documents d JOIN document_revisions r ON r.Id=d.CurrentRevisionId AND r.DocumentId=d.Id WHERE lower(d.Id)=$id;
            """, ("$id", id.ToString("D")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || row.GetInt32(3) != 0
            || !string.Equals(row.GetString(1), row.GetString(4), StringComparison.Ordinal)
            || !string.Equals(ArticleAudioManifest.HashText(row.GetString(1)), row.GetString(5), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved native article/revision is absent, edited or inconsistent.");
        return new(id, row.GetString(0), row.GetString(1), 0, 0, DocumentStatus.Active, false, Guid.Parse(row.GetString(2)), null, null, 0);
    }

    private static async Task<IReadOnlyList<ChirpHistoricalRange>> ReadHistoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        DocumentSnapshot parent, AdmittedChirpArticle admitted, string accountId, string projectId, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT l.ChildDocumentId,l.ChildRevisionId,l.ChildSha256,l.Ordinal,g.ExpectedPartCount,g.SourceRevisionId,g.SourceSha256
            FROM narration_part_groups g JOIN narration_part_links l ON l.GroupId=g.Id
            WHERE lower(g.ParentDocumentId)=$id AND g.Complete=1 ORDER BY l.Ordinal;
            """, ("$id", parent.Id.ToString("D")));
        List<(Guid Id, Guid Revision, string Hash)> children = [];
        using (var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await rows.ReadAsync(token).ConfigureAwait(false))
            {
                if (children.Count >= 200 || rows.GetInt32(3) != children.Count + 1 || rows.GetInt32(4) != admitted.Lineage.History.Count
                    || Guid.Parse(rows.GetString(5)) != parent.CurrentRevisionId || !string.Equals(rows.GetString(6), admitted.Proposal.SourceSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("The original article group changed or is incomplete.");
                children.Add((Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2)));
            }
        if (children.Count == 0) children.Add((parent.Id, parent.CurrentRevisionId!.Value, ArticleAudioManifest.HashText(parent.Text)));
        if (children.Count != admitted.Lineage.History.Count) throw new InvalidOperationException("Historical native coverage changed.");
        List<ChirpHistoricalRange> history = []; int cursor = 0;
        foreach (var child in children)
        {
            var current = await ReadDocumentAsync(connection, transaction, child.Id, token).ConfigureAwait(false);
            if (current.CurrentRevisionId != child.Revision || !string.Equals(ArticleAudioManifest.HashText(current.Text), child.Hash, StringComparison.Ordinal))
                throw new InvalidOperationException("A historical child revision changed.");
            int end = checked(cursor + current.Text.EnumerateRunes().Count());
            var expected = admitted.Lineage.History[history.Count];
            if (expected.DocumentId != current.Id || expected.RevisionId != current.CurrentRevisionId || expected.StartScalar != cursor
                || expected.EndScalarExclusive != end || !string.Equals(expected.TextSha256, child.Hash, StringComparison.Ordinal))
                throw new InvalidOperationException("Historical source coverage does not match the admitted native revision.");
            RequireAttempts(expected, current.Text, admitted.Proposal, accountId, projectId, financial);
            history.Add(expected); cursor = end;
        }
        return history;
    }
}

using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

internal sealed partial class ChirpArticleEvidenceResolver
{
    private static async Task RequireAcceptedFormatAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle article, int ordinal, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var matches = rows.Where(r => Equals(r[1], article.Proposal.PlanSha256) && Equals(r[2], (long)ordinal)).ToArray();
        if (matches.Length != 1 || !Equals(matches[0][16], 1L))
            throw new InvalidOperationException("Article continuation requires the already accepted sentence-format result, never another attempt.");
        var row = matches[0];
        var queue = financial.QueueStates.SingleOrDefault(q => Equals(q.Account, row[5]) && Equals(q.Key, row[7])
            && string.Equals(q.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal));
        var part = article.Proposal.Parts.Single(p => p.CoverageOrdinal == ordinal);
        if (queue is null || queue.Unresolved != 0 || queue.Attempted != 1 || !Equals(queue.LocalAttempt, row[17])
            || !string.Equals(queue.DiagnosticCode, "google-accepted", StringComparison.Ordinal)
            || !string.Equals(queue.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal) || queue.PayloadBytes != part.PayloadBytes)
            throw new InvalidOperationException("Sentence-format acceptance is absent, uncertain or changed.");
        await RequireFinalizedParentOutputAsync(connection, transaction, row, token).ConfigureAwait(false);
    }

    private static async Task RequireFormatParentReuseAsync(SqliteConnection connection, SqliteTransaction transaction,
        AdmittedChirpArticle admitted, AdmittedChirpArticle parent, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT AuthorizationId,ReceiptSha256 FROM chirp_article_receipts WHERE PlanSha256=$plan ORDER BY CoverageOrdinal LIMIT 10001;",
            ("$plan", parent.Proposal.PlanSha256));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        HashSet<Guid> expected = [];
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            Guid id = Guid.ParseExact(reader.GetString(0), "N");
            if (expected.Count >= 10_000 || !expected.Add(id) || admitted.ReusedAudio.Count(x =>
                string.Equals(x.SourcePlanSha256, parent.Proposal.PlanSha256, StringComparison.Ordinal)
                && x.SourceAuthorizationId == id && string.Equals(x.ReceiptSha256, reader.GetString(1), StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("Every accepted format predecessor range must be retained exactly once.");
        }
        var direct = admitted.ReusedAudio.Where(x => string.Equals(x.SourcePlanSha256, parent.Proposal.PlanSha256, StringComparison.Ordinal)).ToArray();
        if (!expected.SetEquals(direct.Select(x => x.SourceAuthorizationId ?? Guid.Empty))
            || parent.ReusedAudio.Any(x => !admitted.ReusedAudio.Contains(x))
            || admitted.ReusedAudio.Any(x => !direct.Contains(x) && !parent.ReusedAudio.Contains(x)))
            throw new InvalidOperationException("Format continuation contains omitted, duplicate or unverified inherited audio.");
    }
}

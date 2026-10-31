using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<ChirpStoredArticle> StoredAsync(SqliteConnection connection, SqliteTransaction transaction, string plan, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT ProposalJson,LineageJson,ReusedAudioJson,AccountId,ProjectId FROM chirp_article_plans WHERE PlanSha256=$plan;", ("$plan", plan));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("No committed internal article plan is available.");
        var proposal = ParseStored<ChirpArticlePlanProposal>(reader.GetString(0));
        var lineage = ParseStored<ChirpPlanLineage>(reader.GetString(1));
        var reused = ParseStored<ChirpReusedArticleAudio[]>(reader.GetString(2));
        if (!string.Equals(proposal.PlanSha256, plan, StringComparison.Ordinal)) throw new InvalidOperationException("Article plan identity changed.");
        ChirpArticlePlanCompiler.RequireIntegrity(proposal);
        return new(new(proposal, lineage, reused, []), reader.GetString(3), reader.GetString(4));
    }

    private static T ParseStored<T>(string text)
    {
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Stored article proof exceeds its inspection bound.");
        return ChirpPolicyFiles.Parse<T>(Encoding.UTF8.GetBytes(text));
    }

    public async Task<bool> HasStoredPlanAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM chirp_article_plans WHERE lower(DocumentId)={0}", documentId.ToString("D"))
            .SingleAsync(cancellationToken).ConfigureAwait(false) != 0;
    }

    private static async Task<string> UniqueStoredPlanAsync(SqliteConnection connection, SqliteTransaction transaction, Guid documentId, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT PlanSha256 FROM chirp_article_plans WHERE lower(DocumentId)=$document LIMIT 2;", ("$document", documentId.ToString("D")));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("No saved internal plan exists for this article.");
        string plan = reader.GetString(0);
        if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Multiple stored plans require explicit plan selection; no automatic export was started.");
        return plan;
    }

    internal async Task<(ChirpArticlePlanProposal Plan, IReadOnlyList<ArticleAcceptedAudio> Ordered)> InspectCompleteAsync(
        Guid documentId, CancellationToken token)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        string plan = await UniqueStoredPlanAsync(connection, transaction, documentId, token).ConfigureAwait(false);
        var stored = await StoredAsync(connection, transaction, plan, token).ConfigureAwait(false);
        var financial = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        await _evidence.ResolveSnapshotAsync(connection, transaction, stored.Article, stored.AccountId, stored.ProjectId, financial, token).ConfigureAwait(false);
        var receipts = await SegmentReceiptsAsync(connection, transaction, stored, financial, token).ConfigureAwait(false);
        return (stored.Article.Proposal, ChirpArticleAudioCoverage.RequireComplete(stored.Article.Proposal, receipts, stored.Article.ReusedAudio));
    }

    private async Task<IReadOnlyList<ChirpSegmentAcceptedAudio>> SegmentReceiptsAsync(SqliteConnection connection,
        SqliteTransaction transaction, ChirpStoredArticle stored, RecoveryFinancialSnapshot financial, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT r.ReceiptJson,r.ReceiptSha256,a.Consumed,a.ClaimedLocalAttemptId,a.ChildKey,a.EnvelopeJson,a.AuthorizationId
            FROM chirp_article_receipts r JOIN chirp_article_owned_attempts a ON a.AuthorizationId=r.AuthorizationId
            WHERE r.PlanSha256=$plan AND a.AccountId=$account AND a.ProjectId=$project ORDER BY r.CoverageOrdinal LIMIT 10001;
            """, ("$plan", stored.Article.Proposal.PlanSha256), ("$account", stored.AccountId), ("$project", stored.ProjectId));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        List<ChirpSegmentAcceptedAudio> receipts = [];
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (receipts.Count >= 10_000) throw new InvalidDataException("Article receipts exceed their inspection bound.");
            var receipt = ParseStored<ChirpSegmentAcceptedAudio>(reader.GetString(0));
            var envelope = ParseStored<GoogleGenerationSubmissionEnvelope>(reader.GetString(5));
            var part = stored.Article.Proposal.Parts.SingleOrDefault(x => x.CoverageOrdinal == receipt.CoverageOrdinal);
            if (!string.Equals(ChirpPolicyValidation.Hash(receipt), reader.GetString(1), StringComparison.Ordinal) || reader.GetInt64(2) != 1
                || !string.Equals(receipt.Audio.LocalAttemptId, reader.GetString(3), StringComparison.Ordinal)
                || !string.Equals(receipt.Audio.IdempotencyKey, reader.GetString(4), StringComparison.Ordinal)
                || !string.Equals(receipt.Audio.PayloadSha256, envelope.CompiledPayloadSha256, StringComparison.Ordinal)
                || !string.Equals(receipt.Audio.AccountId, stored.AccountId, StringComparison.Ordinal)
                || !string.Equals(envelope.AccountId, stored.AccountId, StringComparison.Ordinal)
                || envelope.ChirpAuthorizationId != Guid.ParseExact(reader.GetString(6), "N") || envelope.RecoveryAuthorizationId is not null
                || part is null || part.Range.ReusedReceiptSha256 is not null || receipt.Range != part.Range
                || !string.Equals(receipt.PlanSha256, stored.Article.Proposal.PlanSha256, StringComparison.Ordinal)
                || receipt.Audio.DocumentId != stored.Article.Proposal.DocumentId || receipt.Audio.RevisionId != stored.Article.Proposal.RevisionId
                || !string.Equals(receipt.Audio.TextSha256, part.Range.TextSha256, StringComparison.Ordinal)
                || !string.Equals(receipt.Audio.VoiceName, stored.Article.Proposal.VoiceName, StringComparison.Ordinal)
                || !string.Equals(receipt.Audio.LanguageCode, stored.Article.Proposal.LanguageCode, StringComparison.Ordinal)
                || !string.Equals(Path.GetFullPath(receipt.Audio.AudioPath), Path.Combine(MediaRoot, reader.GetString(6) + ".mp3"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Accepted segment provenance differs from its consumed reservation.");
            ChirpArticleEvidenceResolver.RequireAcceptedQueue(receipt.Audio, financial);
            string? mapping = await RequireReservedDispatchAuditAsync(connection, transaction, Guid.ParseExact(reader.GetString(6), "N"),
                envelope, receipt.Audio.LocalAttemptId, token).ConfigureAwait(false);
            if (!string.Equals(receipt.ExecutionMappingSha256, mapping, StringComparison.Ordinal)) throw ResumeRejected();
            receipts.Add(receipt);
        }
        return receipts.AsReadOnly();
    }
}

using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<ChirpWholeArticleOutput> InspectCompletedWholeArticleOutputAsync(Guid jobId, CancellationToken token = default)
    {
        var job = await ReadWholeArticleJobAsync(jobId, token).ConfigureAwait(false);
        if (job.State != ChirpWholeArticleState.Complete || job.Output is null)
            throw new InvalidOperationException("No complete verified article MP3 exists for this job. Partial audio is preserved and is not a complete article.");
        using (var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false))
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!string.Equals(await CurrentOutputPlanAsync(connection, transaction, job.DocumentId, token).ConfigureAwait(false), job.PlanSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("This completed MP3 belongs to an earlier article revision or plan. Its file is preserved; it cannot be labeled as the current article.");
        }
        ChirpPolicyFiles.RejectLinks(job.Output.Path);
        if (!string.Equals(Path.GetExtension(job.Output.Path), ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The completed output is not an MP3 path.");
        await VerifyWholeArticleOutputAsync(job.Output, token).ConfigureAwait(false);
        return job.Output;
    }

    public async Task<Guid?> FindCompletedWholeArticleAsync(Guid documentId, CancellationToken token = default)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        string? plan = await CurrentOutputPlanAsync(connection, transaction, documentId, token).ConfigureAwait(false);
        if (plan is null) return null;
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT j.JobId FROM chirp_whole_article_jobs j JOIN chirp_article_plans p ON p.PlanSha256=j.PlanSha256
            WHERE lower(p.DocumentId)=$document AND j.PlanSha256=$plan ORDER BY j.rowid DESC LIMIT 101;
            """, ("$document", documentId.ToString("D")), ("$plan", plan));
        List<Guid> ids = [];
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
        if (ids.Count > 100) throw new InvalidDataException("Saved article job history exceeds the bounded UI lookup.");
        foreach (var id in ids)
        {
            var job = await ReadWholeArticleJobAsync(connection, transaction, id, token).ConfigureAwait(false);
            if (job.DocumentId != documentId) throw new InvalidDataException("Saved article output document identity changed.");
            if (job.State == ChirpWholeArticleState.Complete) return id;
        }
        return null;
    }

    private static async Task<string?> CurrentOutputPlanAsync(SqliteConnection connection, SqliteTransaction transaction, Guid documentId, CancellationToken token)
    {
        string plan = await UniqueStoredPlanAsync(connection, transaction, documentId, token).ConfigureAwait(false);
        var stored = await StoredAsync(connection, transaction, plan, token).ConfigureAwait(false);
        var current = await ChirpArticleEvidenceResolver.ReadDocumentAsync(connection, transaction, documentId, token).ConfigureAwait(false);
        return current.CurrentRevisionId == stored.Article.Proposal.RevisionId
            && string.Equals(ArticleAudioManifest.HashText(current.Text), stored.Article.Proposal.SourceSha256, StringComparison.Ordinal)
            ? plan : null;
    }
}

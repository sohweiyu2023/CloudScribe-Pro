using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<ChirpWholeArticleJob?> FindWholeArticleJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        string policy, string plan, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT JobId FROM chirp_whole_article_jobs WHERE PolicySha256=$policy AND PlanSha256=$plan;", ("$policy", policy), ("$plan", plan));
        var id = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return id is string value ? await ReadWholeArticleJobAsync(connection, transaction, Guid.ParseExact(value, "N"), token).ConfigureAwait(false) : null;
    }

    private static async Task<ChirpWholeArticleJob> ReadWholeArticleJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid jobId, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT PolicySha256,PlanSha256,JobJson,JobSha256 FROM chirp_whole_article_jobs WHERE JobId=$id;", ("$id", jobId.ToString("N")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || row.GetString(2).Length > 8192)
            throw new InvalidDataException("The durable article job is missing or exceeds its bound.");
        var job = JsonSerializer.Deserialize<ChirpWholeArticleJob>(row.GetString(2)) ?? throw new InvalidDataException("The article job is unreadable.");
        if (job.JobId != jobId || !Enum.IsDefined(job.State) || job.MaximumAdditionalMinorUnits < 0 || job.CatalogRefreshes is < 0 or > 24
            || !string.Equals(job.PolicySha256, row.GetString(0), StringComparison.Ordinal)
            || !string.Equals(job.PlanSha256, row.GetString(1), StringComparison.Ordinal)
            || !string.Equals(ChirpPolicyValidation.Hash(job), row.GetString(3), StringComparison.Ordinal))
            throw new InvalidDataException("The durable article job identity or integrity changed.");
        await row.DisposeAsync().ConfigureAwait(false);
        using var audit = ChirpArticleSql.Command(connection, transaction,
            "SELECT JobSha256 FROM chirp_whole_article_events WHERE JobId=$id ORDER BY Sequence DESC LIMIT 1;", ("$id", jobId.ToString("N")));
        if (!string.Equals(await audit.ExecuteScalarAsync(token).ConfigureAwait(false) as string, ChirpPolicyValidation.Hash(job), StringComparison.Ordinal))
            throw new InvalidDataException("The durable article checkpoint differs from its append-only history.");
        return job;
    }

    private static async Task WriteWholeArticleJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpWholeArticleJob job, bool insert, CancellationToken token)
    {
        string sql = insert ? "INSERT INTO chirp_whole_article_jobs VALUES($id,$policy,$plan,$json,$hash);"
            : "UPDATE chirp_whole_article_jobs SET JobJson=$json,JobSha256=$hash WHERE JobId=$id AND PolicySha256=$policy AND PlanSha256=$plan;";
        using var command = ChirpArticleSql.Command(connection, transaction, sql, ("$id", job.JobId.ToString("N")),
            ("$policy", job.PolicySha256), ("$plan", job.PlanSha256), ("$json", JsonSerializer.Serialize(job)), ("$hash", ChirpPolicyValidation.Hash(job)));
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("Article job checkpoint was not persisted.");
        using var audit = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO chirp_whole_article_events(JobId,Sequence,JobJson,JobSha256)
            SELECT $id,COALESCE(MAX(Sequence),0)+1,$json,$hash FROM chirp_whole_article_events WHERE JobId=$id;
            """, ("$id", job.JobId.ToString("N")), ("$json", JsonSerializer.Serialize(job)), ("$hash", ChirpPolicyValidation.Hash(job)));
        if (await audit.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("Article consent or transition audit was not persisted.");
    }

    private async Task<ChirpWholeArticleJob> TransitionWholeArticleAsync(ChirpWholeArticleJob expected,
        ChirpWholeArticleState state, int? ordinal, string reason, CancellationToken token, ChirpWholeArticleOutput? output = null, bool countRefresh = false)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadWholeArticleJobAsync(connection, transaction, expected.JobId, token).ConfigureAwait(false);
        if (current != expected) throw new InvalidOperationException("Another worker changed the article job. Reconcile before continuing.");
        if (clock.GetUtcNow() < current.LastObservedAtUtc) throw new InvalidOperationException("Article job clock rollback; no request or checkpoint may proceed.");
        var next = current with
        {
            State = state,
            ActiveOrdinal = ordinal,
            ReasonCode = reason,
            LastObservedAtUtc = clock.GetUtcNow(),
            Output = output ?? current.Output,
            CatalogRefreshes = checked(current.CatalogRefreshes + (countRefresh ? 1 : 0))
        };
        await WriteWholeArticleJobAsync(connection, transaction, next, false, token).ConfigureAwait(false);
        transaction.Commit(); return next;
    }
}

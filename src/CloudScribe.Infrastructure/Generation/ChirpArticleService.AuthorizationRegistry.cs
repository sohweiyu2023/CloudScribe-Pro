using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task RequireAuthorizationRegistryAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<object?[]> attempts, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT AuthorizationId FROM chirp_article_authorizations ORDER BY AuthorizationId LIMIT 10001;");
        using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        HashSet<string> ids = new(StringComparer.Ordinal);
        while (await rows.ReadAsync(token).ConfigureAwait(false))
        {
            string id = rows.GetString(0);
            if (ids.Count == 10_000 || !Guid.TryParseExact(id, "N", out _) || !ids.Add(id))
                throw new InvalidOperationException("The retained authorization registry is malformed or ambiguous.");
        }
        if (!ids.SetEquals(attempts.Select(x => (string)x[0]!)))
            throw new InvalidOperationException("A retained authorization lost its exact financial owner.");
    }
}

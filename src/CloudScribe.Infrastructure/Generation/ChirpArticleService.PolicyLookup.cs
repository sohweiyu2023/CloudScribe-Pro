using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<ChirpArticleAdmissionPolicy> AuthorizationPolicyAsync(Guid authorization, CancellationToken token)
    {
        if (admission is not IChirpArticleAdmissionCatalog) return await PolicyAsync(token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PolicySha256 FROM chirp_article_owned_attempts WHERE AuthorizationId=$id;";
        command.Parameters.AddWithValue("$id", authorization.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("No persisted article authorization owns this request.");
        string hash = reader.GetString(0);
        if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Article authorization ownership is ambiguous.");
        return await PolicyAsync(token, policySha256: hash).ConfigureAwait(false);
    }
}
